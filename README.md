# Dippin' Dots

A small Dalamud plugin for FFXIV. While the retainer sell list is open, it draws a dot in the
bottom-left corner of each slot in the retainer bag and the Expanded player inventory. The dot is
colored by what the slot is worth: the item's 7-day median sale price on your home world
([Universalis](https://universalis.app)) times the stack size.

- **Filled dot:** red (low) to green (high), on a 7-step gradient (250 / 500 / 1k / 2.5k / 5k / 10k gil by default).
- **Hollow ring:** the price is shaky: fewer than 3 sales in 7 days, or the median and mean disagree by more than 2×.
- **Grey ring:** price still loading. **Dim grey dot:** no market data.
- Untradeable items and empty slots get no dot. Dots hide under tooltips and context menus.

Prices are cached per item for 2 hours, so opening retainers doesn't hammer Universalis.

## Install

In-game: `/xlsettings` → **Experimental** → **Custom Plugin Repositories**, add

```
https://raw.githubusercontent.com/darionbuilds/DippinDots/main/repo.json
```

Save, then install **Dippin' Dots** from `/xlplugins`.

## Use

- `/dots`: toggle the dots
- `/dots config`: settings window (also the buttons in the plugin installer)
- Options: `on`, `off`, `stack`, `unit`, `inv`, `sell`, `bands 250 500 1000 …`, `ratio 2`, `status`, `help`
- `/dots probe`: writes a diagnostic dump to the plugin config folder (`probe overlay`, `probe prices` too)

Only the Expanded inventory layout is supported for your own bag.

## Build

Needs the .NET 10 SDK. `dotnet build -c Release` writes `bin/Release/DippinDots/latest.zip`.
`release.ps1` publishes a new version (bump `<Version>` in the csproj first).
