Features

## Blacklist a site's thumbnails

- Adds a new button on the tab right-click popup for browser tabs that permanently suppresses every thumbnail source for a site's domain — page images, YouTube thumbnails, and screenshots alike — so sites that only ever report useless images (generic banners, logos) fall back to the plain favicon.
- The choice is remembered per domain and reappears in the saved-state manager under "Blacklisted thumbnail domain", where it can be cleared. The button icon lights up when the current tab's domain is blocked.
- Blocking a domain immediately clears any cached video thumbnail for its tabs so the blacklist takes effect without waiting for a refresh.

## Run-on-startup now uses a visible Startup-folder batch file

- Switches auto-start from a hidden `Run` registry value to a `.bat` file dropped in your Startup folder, so the exact exe that launches at login is visible and editable by you, and any older registry entry is cleared out automatically.
- The settings page now shows exactly which exe startup will run, and warns when that copy is a different exe than the one you're currently using — re-checking the box switches startup to the running copy.
- Ticking "Run on startup" targets whichever exe you launched, while installing hands startup ownership to the installed copy.

## Install button reflects what's already installed

- The Install button changes to "Reinstall / update" and shows the installed version and its path once a copy exists, instead of always reading "Install TabDesktop".
- When the installed version differs from the build you're running, the status line notes which version this build is, making it obvious when an update is available.
