#!/usr/bin/env bash
#
# MyVpn Linux server — a self-contained WireGuard server with NAT.
#
# Mirrors the Windows app's security model: you generate the key pair on the phone
# (the MyVpn app's "Generate key" button) and give the server only the PUBLIC key.
# The client config this script emits uses the same __CLIENT_PRIVATE_KEY__ placeholder
# the Android app fills in on import.
#
# Debian/Ubuntu. Run as root.
#
# Usage:
#   sudo ./myvpn-server.sh setup [PUBLIC_ENDPOINT]   # install + configure the server
#   sudo ./myvpn-server.sh add-client NAME PUBKEY     # add a peer from the phone's public key
#   sudo ./myvpn-server.sh add-client NAME --generate # (fallback) let the server make the key
#   sudo ./myvpn-server.sh show-client NAME           # print the client .conf + QR
#   sudo ./myvpn-server.sh list
#   sudo ./myvpn-server.sh remove-client NAME
#
set -euo pipefail

WG_IF="wg0"
WG_PORT="51820"
WG_SUBNET="10.8.0"
WG_SERVER_IP="1"
DNS_SERVERS="1.1.1.1, 1.0.0.1"
PLACEHOLDER="__CLIENT_PRIVATE_KEY__"

CONF_DIR="/etc/wireguard"
CLIENTS_DIR="$CONF_DIR/clients"
CONF_FILE="$CONF_DIR/$WG_IF.conf"
ENDPOINT_FILE="$CONF_DIR/endpoint"
KEYS_DIR="$CONF_DIR/keys"

log()  { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[!]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[x]\033[0m %s\n' "$*" >&2; exit 1; }

require_root() { [ "$(id -u)" -eq 0 ] || die "Run this as root (sudo)."; }

outbound_iface() {
  ip -4 route ls default 2>/dev/null | sed -n 's/.*dev \([^ ]*\).*/\1/p' | head -n1
}

# Next free last-octet in the tunnel subnet (skips .0, .1 and used ones).
next_ip() {
  local used last
  used=$(grep -h '^AllowedIPs' "$CONF_FILE" 2>/dev/null | sed -n "s#.*${WG_SUBNET}\.\([0-9]\+\).*#\1#p" | sort -n || true)
  last=$((WG_SERVER_IP + 1))
  while echo "$used" | grep -qx "$last"; do last=$((last + 1)); done
  [ "$last" -le 254 ] || die "No free tunnel addresses left."
  echo "${WG_SUBNET}.${last}"
}

require_setup() {
  [ -f "$CONF_FILE" ] || die "Server not set up yet. Run: sudo $0 setup [PUBLIC_ENDPOINT]"
}

cmd_setup() {
  require_root
  local endpoint="${1:-}"

  log "Installing packages (wireguard, qrencode, iptables)..."
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -y
  apt-get install -y wireguard qrencode iptables iproute2

  umask 077
  mkdir -p "$CLIENTS_DIR" "$KEYS_DIR"

  if [ ! -f "$KEYS_DIR/server.key" ]; then
    log "Generating server key pair..."
    wg genkey | tee "$KEYS_DIR/server.key" | wg pubkey > "$KEYS_DIR/server.pub"
  fi
  local server_priv server_pub
  server_priv=$(cat "$KEYS_DIR/server.key")
  server_pub=$(cat "$KEYS_DIR/server.pub")

  local out
  out=$(outbound_iface)
  [ -n "$out" ] || die "Could not determine the outbound network interface."
  log "Outbound interface: $out"

  log "Enabling IP forwarding..."
  cat > /etc/sysctl.d/99-myvpn.conf <<EOF
net.ipv4.ip_forward = 1
net.ipv6.conf.all.forwarding = 1
EOF
  sysctl --system >/dev/null

  log "Writing $CONF_FILE ..."
  cat > "$CONF_FILE" <<EOF
[Interface]
Address = ${WG_SUBNET}.${WG_SERVER_IP}/24
ListenPort = ${WG_PORT}
PrivateKey = ${server_priv}
PostUp   = iptables -A FORWARD -i %i -j ACCEPT; iptables -A FORWARD -o %i -j ACCEPT; iptables -t nat -A POSTROUTING -o ${out} -j MASQUERADE
PostDown = iptables -D FORWARD -i %i -j ACCEPT; iptables -D FORWARD -o %i -j ACCEPT; iptables -t nat -D POSTROUTING -o ${out} -j MASQUERADE
EOF
  chmod 600 "$CONF_FILE"

  if [ -n "$endpoint" ]; then
    echo "${endpoint}:${WG_PORT}" > "$ENDPOINT_FILE"
    log "Public endpoint set to ${endpoint}:${WG_PORT}"
  fi

  log "Starting wg-quick@$WG_IF ..."
  # Do NOT auto-start on boot: the Windows app / shortcut starts and stops it explicitly,
  # otherwise any WSL start would silently bring the server back up.
  systemctl disable "wg-quick@$WG_IF" >/dev/null 2>&1 || true
  systemctl restart "wg-quick@$WG_IF"

  # Open the UDP port in ufw if it is active.
  if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw allow "${WG_PORT}/udp" >/dev/null || true
    log "ufw: allowed ${WG_PORT}/udp"
  fi

  log "Server is up. Public key:"
  echo "    $server_pub"
  if [ -z "$endpoint" ]; then
    warn "No public endpoint set. Set it with:"
    warn "    echo 'YOUR.HOST.OR.IP:${WG_PORT}' > ${ENDPOINT_FILE}"
    warn "or re-run: sudo $0 setup YOUR.HOST.OR.IP"
  fi
}

cmd_add_client() {
  require_root
  require_setup
  local name="${1:-}" pub="${2:-}"
  [ -n "$name" ] || die "Usage: $0 add-client NAME PUBKEY | NAME --generate"
  [[ "$name" =~ ^[A-Za-z0-9_.-]+$ ]] || die "Name may only contain letters, digits, . _ -"
  [ -f "$CLIENTS_DIR/$name.pub" ] && die "Client '$name' already exists."

  local ip
  ip=$(next_ip)

  if [ "$pub" = "--generate" ]; then
    warn "Generating the key pair on the server (the server will know this client's private key)."
    wg genkey | tee "$CLIENTS_DIR/$name.key" | wg pubkey > "$CLIENTS_DIR/$name.pub"
    pub=$(cat "$CLIENTS_DIR/$name.pub")
  else
    [ -n "$pub" ] || die "Usage: $0 add-client NAME PUBKEY | NAME --generate"
    # Validate: 32-byte base64 (44 chars ending in '=').
    echo "$pub" | base64 -d >/dev/null 2>&1 || die "The public key is not valid base64."
    [ "$(echo "$pub" | base64 -d | wc -c)" -eq 32 ] || die "The public key is not a 32-byte WireGuard key."
    echo "$pub" > "$CLIENTS_DIR/$name.pub"
  fi

  echo "$ip" > "$CLIENTS_DIR/$name.ip"

  log "Adding peer '$name'  ->  ${ip}/32"
  cat >> "$CONF_FILE" <<EOF

[Peer]
# $name
PublicKey = ${pub}
AllowedIPs = ${ip}/32
EOF

  # Apply live without dropping the tunnel.
  wg set "$WG_IF" peer "$pub" allowed-ips "${ip}/32"

  log "Done. Print the client config (with QR) with:  $0 show-client $name"
}

cmd_show_client() {
  require_root
  local name="${1:-}" png="${2:-}"
  [ -n "$name" ] || die "Usage: $0 show-client NAME [OUTPUT.png]"
  [ -f "$CLIENTS_DIR/$name.pub" ] || die "No such client: $name"

  local ip server_pub endpoint client_priv
  ip=$(cat "$CLIENTS_DIR/$name.ip")
  server_pub=$(cat "$KEYS_DIR/server.pub")
  endpoint=$(cat "$ENDPOINT_FILE" 2>/dev/null || true)
  [ -n "$endpoint" ] || die "Public endpoint not set. See: $CONF_DIR/endpoint"

  if [ -f "$CLIENTS_DIR/$name.key" ]; then
    client_priv=$(cat "$CLIENTS_DIR/$name.key")
  else
    client_priv="$PLACEHOLDER"
  fi

  local conf
  conf=$(
    cat <<EOF
[Interface]
PrivateKey = ${client_priv}
Address = ${ip}/32
DNS = ${DNS_SERVERS}

[Peer]
PublicKey = ${server_pub}
AllowedIPs = 0.0.0.0/0, ::/0
Endpoint = ${endpoint}
PersistentKeepalive = 25
EOF
  )

  printf '\n%s\n\n' "----- $name.conf -----"
  printf '%s\n\n' "$conf"

  if [ -n "$png" ]; then
    command -v qrencode >/dev/null || die "qrencode not installed (apt-get install qrencode)."
    qrencode -o "$png" -t png -s 8 <<< "$conf"
    printf '%s\n' "QR image written to: $png"
  elif command -v qrencode >/dev/null; then
    printf '%s\n' "----- scan with the MyVpn app -----"
    qrencode -t ansiutf8 <<< "$conf"
  fi

  printf '%s\n' "(Hint: if PrivateKey is ${PLACEHOLDER}, the MyVpn app fills in the device key.)"
}

cmd_list() {
  require_root
  local f
  for f in "$CLIENTS_DIR"/*.ip; do
    [ -e "$f" ] || continue
    local n
    n=$(basename "$f" .ip)
    printf '%-20s %s\n' "$n" "$(cat "$f")"
  done
}

cmd_remove_client() {
  require_root
  local name="${1:-}"
  [ -n "$name" ] || die "Usage: $0 remove-client NAME"
  [ -f "$CLIENTS_DIR/$name.pub" ] || die "No such client: $name"

  local pub
  pub=$(cat "$CLIENTS_DIR/$name.pub")
  wg set "$WG_IF" peer "$pub" remove || true

  # Drop the [Peer] paragraph whose comment line matches "# <name>".
  awk -v name="$name" '
    BEGIN { RS=""; ORS="\n\n" }
    {
      n = split($0, lines, "\n");
      for (i = 1; i <= n; i++)
        if (lines[i] == "# " name) next;
      print;
    }' "$CONF_FILE" > "$CONF_FILE.tmp" && mv "$CONF_FILE.tmp" "$CONF_FILE"

  rm -f "$CLIENTS_DIR/$name.pub" "$CLIENTS_DIR/$name.ip" "$CLIENTS_DIR/$name.key"
  log "Removed client '$name'."
}

cmd_set_endpoint() {
  require_root
  require_setup
  local ep="${1:-}"
  [ -n "$ep" ] || die "Usage: $0 set-endpoint HOST[:PORT]"
  echo "$ep" > "$ENDPOINT_FILE"
  log "Public endpoint set to $ep"
}

# Machine-readable status for the Windows app.
cmd_status() {
  require_root
  require_setup
  if wg show "$WG_IF" >/dev/null 2>&1; then echo "STATE=UP"; else echo "STATE=DOWN"; fi
  echo "PUB=$(cat "$KEYS_DIR/server.pub" 2>/dev/null)"
  echo "ENDPOINT=$(cat "$ENDPOINT_FILE" 2>/dev/null)"
  echo "PORT=${WG_PORT}"
  echo "SUBNET=${WG_SUBNET}.0/24"
}

# Machine-readable peer list for the Windows app (includes disabled peers).
cmd_dump() {
  require_root
  require_setup
  local -A LIVE=()
  local pub psk ep aips hs rx tx ka
  while IFS=$'\t' read -r pub psk ep aips hs rx tx ka; do
    [ -n "$pub" ] && LIVE["$pub"]="${hs}|${rx}|${tx}"
  done < <(wg show "$WG_IF" dump 2>/dev/null | tail -n +2)

  local f n ip enabled
  for f in "$CLIENTS_DIR"/*.pub; do
    [ -e "$f" ] || continue
    n=$(basename "$f" .pub)
    pub=$(cat "$f")
    ip=$(cat "$CLIENTS_DIR/$n.ip" 2>/dev/null)
    if [ -n "${LIVE[$pub]:-}" ]; then
      IFS='|' read -r hs rx tx <<< "${LIVE[$pub]}"
      enabled=yes
    else
      hs=0; rx=0; tx=0; enabled=no
    fi
    printf 'PEER\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$n" "$pub" "$ip" "$enabled" "$hs" "$rx" "$tx"
  done
}

usage() {
  sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
}

main() {
  local cmd="${1:-}"
  shift || true
  case "$cmd" in
    setup)         cmd_setup "${1:-}" ;;
    add-client)    cmd_add_client "${1:-}" "${2:-}" ;;
    show-client)   cmd_show_client "${1:-}" "${2:-}" ;;
    remove-client) cmd_remove_client "${1:-}" ;;
    set-endpoint)  cmd_set_endpoint "${1:-}" ;;
    status)        cmd_status ;;
    dump)          cmd_dump ;;
    list)          cmd_list ;;
    ""|-h|--help|help) usage ;;
    *) die "Unknown command: $cmd (try --help)" ;;
  esac
}

main "$@"
