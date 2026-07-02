# TabDesktop

**[Download here](https://github.com/sliftist/tabdesktop/releases/latest)**

Browser-style tab strips for your Windows desktop. TabDesktop shows a strip at the top of each monitor listing your open windows as tabs — with icons, favicons, and live thumbnails — so you can see and switch between everything at a glance.

![Many tabs](pictures/Many%20tabs.png)

## Features

- One tab per open window, grouped and titled sensibly (e.g. terminals show their working directory)
- Favicons for browser tabs and thumbnails for videos, cached to disk
- Hang-safe window polling — a frozen app never freezes the strip
- Starts without stealing focus

## Installing

Download the latest release from the [releases page](https://github.com/sliftist/tabdesktop/releases/latest) and run it.

## Development

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download)
2. Install [Node.js](https://nodejs.org/)
3. Install [Yarn](https://yarnpkg.com/): `npm install -g yarn`
4. `git clone https://github.com/sliftist/tabdesktop.git`
5. `cd tabdesktop`
6. `yarn start`
