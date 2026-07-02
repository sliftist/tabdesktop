# TabDesktop

Browser-style tab strips for your Windows desktop. TabDesktop shows a strip at the top of each monitor listing your open windows as tabs — with icons, favicons, and live thumbnails — so you can see and switch between everything at a glance.

![Many tabs](pictures/Many%20tabs.png)

## Features

- One tab per open window, grouped and titled sensibly (e.g. terminals show their working directory)
- Favicons for browser tabs and thumbnails for videos, cached to disk
- Hang-safe window polling — a frozen app never freezes the strip
- Starts without stealing focus

## Running

```
yarn start
```

Build a release with `yarn release`, or publish a GitHub release with `yarn publish-release`.
