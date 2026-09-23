package com.myvpn.client

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.VpnService
import android.os.Bundle
import android.provider.Settings
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.myvpn.client.databinding.ActivityMainBinding
import com.wireguard.android.backend.Backend
import com.wireguard.android.backend.GoBackend
import com.wireguard.android.backend.Tunnel
import com.wireguard.config.Config
import com.wireguard.crypto.Key
import com.wireguard.crypto.KeyPair
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.ByteArrayInputStream

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var secretStore: SecretStore
    private var privateKeyBase64: String? = null

    /**
     * The WireGuard engine. GoBackend runs wireguard-go on top of a VpnService that the
     * library declares in its own manifest, so no VpnService subclass is needed here.
     */
    private val backend: Backend by lazy { GoBackend(applicationContext) }

    private val tunnel = object : Tunnel {
        override fun getName(): String = TUNNEL_NAME

        override fun onStateChange(newState: Tunnel.State) {
            runOnUiThread { renderState(newState) }
        }
    }

    private var currentState = Tunnel.State.DOWN
    private var pendingConfig: Config? = null
    private var statsJob: Job? = null
    private var busy = false

    private val vpnPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            val config = pendingConfig
            pendingConfig = null
            if (result.resultCode == Activity.RESULT_OK && config != null) {
                startTunnel(config)
            } else {
                toast(getString(R.string.vpn_permission_denied))
                renderState(Tunnel.State.DOWN)
            }
        }

    private val scanLauncher = registerForActivityResult(ScanContract()) { result ->
        if (result.contents != null) {
            binding.configInput.setText(result.contents)
            startTunnelIfPossible()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Keep the configuration (and private key) out of screenshots and the recents thumbnail.
        window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)

        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        secretStore = SecretStore(applicationContext)
        binding.configInput.setText(secretStore.loadConfig().orEmpty())
        privateKeyBase64 = secretStore.loadPrivateKey()

        binding.toggleButton.setOnClickListener { if (currentState == Tunnel.State.UP) disconnect() else connect() }
        binding.scanButton.setOnClickListener { scanQrCode() }
        binding.pasteButton.setOnClickListener { pasteFromClipboard() }
        binding.generateKeyButton.setOnClickListener { generateKey() }
        binding.copyKeyButton.setOnClickListener { copyPublicKey() }
        binding.vpnSettingsButton.setOnClickListener { openVpnSettings() }
        updateKeyUi()

        refreshState()
    }

    override fun onResume() {
        super.onResume()
        if (!busy) refreshState()
    }

    override fun onDestroy() {
        statsJob?.cancel()
        super.onDestroy()
    }

    private fun scanQrCode() {
        val options = ScanOptions().apply {
            setDesiredBarcodeFormats(ScanOptions.QR_CODE)
            setPrompt(getString(R.string.scan_prompt))
            setBeepEnabled(false)
            setOrientationLocked(true)
        }
        scanLauncher.launch(options)
    }

    private fun pasteFromClipboard() {
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
        val text = clipboard?.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString()
        if (text.isNullOrBlank()) {
            toast(getString(R.string.clipboard_empty))
            return
        }
        binding.configInput.setText(text)
        startTunnelIfPossible()
    }

    private fun connect() {
        val raw = binding.configInput.text.toString()
        if (raw.isBlank()) {
            toast(getString(R.string.need_config))
            return
        }

        // A server-issued config has no private key; fill in this device's key.
        val resolved = resolvePlaceholder(raw) ?: return

        val config = try {
            Config.parse(ByteArrayInputStream(resolved.toByteArray()))
        } catch (e: Exception) {
            toast(getString(R.string.invalid_config, e.message ?: ""))
            return
        }

        // Show every peer's endpoint and routes so a malicious QR/paste is visible before connecting.
        val details = config.peers.joinToString("\n\n") { p ->
            "Endpoint: ${p.endpoint?.toString() ?: "(none)"}\nRoutes: ${p.allowedIps.joinToString(", ")}"
        }.ifBlank { "(no peers)" }

        AlertDialog.Builder(this)
            .setTitle(R.string.confirm_title)
            .setMessage(getString(R.string.confirm_message, details))
            .setPositiveButton(R.string.connect) { _, _ -> proceedConnect(raw, config) }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    /** Replaces the server's private-key placeholder with this device's key. Returns null on failure. */
    private fun resolvePlaceholder(raw: String): String? {
        if (!raw.contains(PRIVATE_KEY_PLACEHOLDER))
            return raw

        val privateKey = privateKeyBase64
        if (privateKey.isNullOrBlank()) {
            toast(getString(R.string.need_key))
            return null
        }
        return raw.replace(PRIVATE_KEY_PLACEHOLDER, privateKey)
    }

    private fun proceedConnect(rawConfig: String, config: Config) {
        // Store the config as received (placeholder, no secret); the key is stored separately.
        try {
            secretStore.saveConfig(rawConfig)
        } catch (e: Exception) {
            toast(getString(R.string.save_failed, e.message ?: ""))
        }

        // Ask for the system VPN consent dialog when it has not been granted yet.
        val prepareIntent = VpnService.prepare(this)
        if (prepareIntent != null) {
            pendingConfig = config
            vpnPermissionLauncher.launch(prepareIntent)
        } else {
            startTunnel(config)
        }
    }

    private fun openVpnSettings() {
        try {
            startActivity(Intent(Settings.ACTION_VPN_SETTINGS))
        } catch (e: Exception) {
            toast(getString(R.string.open_vpn_settings_failed))
        }
    }

    private fun generateKey() {
        if (!privateKeyBase64.isNullOrBlank()) {
            AlertDialog.Builder(this)
                .setTitle(R.string.regen_title)
                .setMessage(R.string.regen_message)
                .setPositiveButton(R.string.generate_key) { _, _ -> doGenerateKey() }
                .setNegativeButton(android.R.string.cancel, null)
                .show()
        } else {
            doGenerateKey()
        }
    }

    private fun doGenerateKey() {
        try {
            val privateKey = KeyPair().privateKey.toBase64()
            privateKeyBase64 = privateKey
            secretStore.savePrivateKey(privateKey)
            updateKeyUi()
            toast(getString(R.string.key_generated))
        } catch (e: Exception) {
            toast(getString(R.string.key_generate_failed, e.message ?: ""))
        }
    }

    private fun copyPublicKey() {
        val publicKey = currentPublicKey()
        if (publicKey == null) {
            toast(getString(R.string.need_key))
            return
        }
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
        clipboard?.setPrimaryClip(ClipData.newPlainText("WireGuard public key", publicKey))
        toast(getString(R.string.public_key_copied))
    }

    private fun updateKeyUi() {
        binding.myKey.text = currentPublicKey() ?: getString(R.string.key_none)
    }

    private fun currentPublicKey(): String? {
        val privateKey = privateKeyBase64 ?: return null
        return try {
            KeyPair(Key.fromBase64(privateKey)).publicKey.toBase64()
        } catch (e: Exception) {
            null
        }
    }

    private fun startTunnelIfPossible() {
        if (currentState != Tunnel.State.UP) connect()
    }

    private fun startTunnel(config: Config) {
        busy = true
        binding.status.setText(R.string.status_connecting)
        binding.toggleButton.isEnabled = false

        lifecycleScope.launch {
            val result = withContext(Dispatchers.IO) {
                runCatching { backend.setState(tunnel, Tunnel.State.UP, config) }
            }
            busy = false
            binding.toggleButton.isEnabled = true
            result
                .onSuccess { renderState(it) }
                .onFailure {
                    toast(getString(R.string.start_failed, it.message ?: ""))
                    renderState(Tunnel.State.DOWN)
                }
        }
    }

    private fun disconnect() {
        busy = true
        binding.toggleButton.isEnabled = false

        lifecycleScope.launch {
            val result = withContext(Dispatchers.IO) {
                runCatching { backend.setState(tunnel, Tunnel.State.DOWN, null) }
            }
            busy = false
            binding.toggleButton.isEnabled = true
            result
                .onSuccess { renderState(Tunnel.State.DOWN) }
                .onFailure {
                    toast(getString(R.string.stop_failed, it.message ?: ""))
                    renderState(Tunnel.State.DOWN)
                }
        }
    }

    private fun refreshState() {
        lifecycleScope.launch {
            val state = withContext(Dispatchers.IO) {
                runCatching { backend.getState(tunnel) }.getOrDefault(Tunnel.State.DOWN)
            }
            renderState(state)
        }
    }

    private fun renderState(state: Tunnel.State) {
        currentState = state
        when (state) {
            Tunnel.State.UP -> {
                binding.status.setText(R.string.status_connected)
                binding.status.setTextColor(ContextCompat.getColor(this, R.color.connected))
                binding.toggleButton.text = getString(R.string.disconnect)
                binding.scanButton.isEnabled = false
                binding.pasteButton.isEnabled = false
                startStats()
            }

            Tunnel.State.DOWN -> {
                binding.status.setText(R.string.status_disconnected)
                binding.status.setTextColor(ContextCompat.getColor(this, R.color.disconnected))
                binding.toggleButton.text = getString(R.string.connect)
                binding.scanButton.isEnabled = true
                binding.pasteButton.isEnabled = true
                stopStats()
                binding.stats.text = ""
            }

            else -> Unit
        }
    }

    private fun startStats() {
        if (statsJob?.isActive == true) return
        statsJob = lifecycleScope.launch {
            while (isActive) {
                val stats = withContext(Dispatchers.IO) {
                    runCatching { backend.getStatistics(tunnel) }.getOrNull()
                }
                if (stats != null) {
                    binding.stats.text = getString(
                        R.string.stats_format,
                        formatBytes(stats.totalRx()),
                        formatBytes(stats.totalTx()),
                    )
                }
                delay(2000)
            }
        }
    }

    private fun stopStats() {
        statsJob?.cancel()
        statsJob = null
    }

    private fun formatBytes(bytes: Long): String {
        if (bytes < 1024) return "$bytes B"
        val units = arrayOf("KB", "MB", "GB", "TB")
        var value = bytes.toDouble() / 1024
        var index = 0
        while (value >= 1024 && index < units.size - 1) {
            value /= 1024
            index++
        }
        return String.format("%.1f %s", value, units[index])
    }

    private fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_LONG).show()

    private companion object {
        const val TUNNEL_NAME = "myvpn"
        const val PRIVATE_KEY_PLACEHOLDER = "__CLIENT_PRIVATE_KEY__"
    }
}
