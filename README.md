# Pedal C-fader

A 2 to 6 channel crossfader effect for ReBuzz. It's a managed replica of
**kibibu Slice n Dice** (Cameron Foale), worked out from the original
32-bit Buzz DLL.

## Use

1. Connect sources to the machine's input pins **A** to **F** (choose the pin in
   the connection's channel menu). Several sources on the same pin are summed.
2. Set **Channels** to the number of pins you want the fader to cover.
3. Sweep **Fade**. 0 is A and 240 is the last active channel. The positions in
   between crossfade between neighbouring channels.

## Parameters

| Name     | Range | Default | Notes |
|----------|-------|---------|-------|
| Fade     | 0–240 | 0 | Crossfade position. Each channel lands on a whole value: 2 ch = 0/240, 3 ch = 0/120/240, 4 ch = every 80, 5 ch = every 60, 6 ch = every 48. |
| Inertia  | 0–240 | 16 (½ tick) | Glide time to a new Fade position, in ticks: None, 1/32 tick … 104 ticks. Uses the same value table as Slice n Dice. |
| Channels | 2–6   | 3 | How many pins the fader spans. Pins above it are muted and show as *(unused)*. |
| Curve    | Linear / Equal Power | Linear | Linear matches the original, where gains sum to 1 and level dips about 6 dB mid-fade on uncorrelated material. Equal Power keeps the level steady. |

## Mapping from Slice n Dice

| Slice n Dice | Pedal C-fader |
|---|---|
| 12 inputs, each assigned to channel A/B/C via `Input1`–`Input12` | Pins A–F. Assign a source by picking the pin when you connect it. |
| `Fade` 0–192 across A–B–C | `Fade` 0–240 across A to the last active channel (Slice n Dice value × 1.25 at 3 channels) |
| `Inertia` 0–240, default 16 | Same values and the same tick table |
| Linear triangular gains | Same, plus an optional Equal Power curve |
| Gain ramps linearly over Inertia ticks; 32 samples at Inertia None | Same |
| Changing Inertia mid-glide rescales the remaining time | Same |

## Why all 6 pins always exist

ReBuzz indexes a machine's input buffers by each connection's saved pin number.
If the pin count followed **Channels**, a song saved with something on pin E,
then reloaded with Channels = 3, would crash before the machine ever runs. So
the pin count is fixed at 6 and **Channels** only decides which pins are heard.

## Build

`dotnet build -c Release` with ReBuzz closed. The DLL deploys to
`C:\Program Files\ReBuzz\Gear\Effects\Pedal C-fader.NET.dll`. Check the version
in the right-click **About...** box after deploying. If ReBuzz was running, the
copy fails silently and you'll still have the old DLL.

Files: `PedalCFader.cs` (machine and ReBuzz glue), `FaderCore.cs` (fader maths
with no SDK dependency).
