#!/bin/sh
set -e
mkdir -p /var/lib/aiursoft-nopkg
chown www-data:www-data /var/lib/aiursoft-nopkg

# Bootstrap config if missing (matching Docker entrypoint pattern)
if [ ! -f /etc/aiursoft-nopkg/appsettings.json ]; then
    cp /usr/share/aiursoft-nopkg/appsettings.json /etc/aiursoft-nopkg/appsettings.json
fi
# Replace /usr/share copy with symlink to /etc
rm -f /usr/share/aiursoft-nopkg/appsettings.json
ln -sf /etc/aiursoft-nopkg/appsettings.json /usr/share/aiursoft-nopkg/appsettings.json
