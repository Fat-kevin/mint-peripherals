# UI Redesign Plan: "Copper Console" (铜台)

## Core Insight

The current "Graphite Signal Desk" fails because it's **cold, flat, and generic**. Three layers of
blue-grey rectangles with thin borders create monotony, not depth. Device colors exist only as
3px strips — skin-deep. English all-caps kickers feel templated. The result reads as a developer
admin panel, not a premium device console.

## New Direction: "Copper Console"

A warm, rich dark environment inspired by precision audio equipment and high-end instrumentation.
Warm charcoal surfaces with copper accents. Each device owns its color territory visibly.
Typography is confident and clean without bilingual affectations.

## Color System (Dark)

### Surfaces — warm shift from blue-grey to warm-grey
| Token | Old (blue-grey) | New (warm) | Change |
|---|---|---|---|
| Canvas | #131418 | #16130F | Warm charcoal |
| Chrome | #0E1014 | #110E0B | Darker warm |
| TitleBar | #0A0B0E | #0C0A08 | Darkest warm |
| Card | #1B1D23 | #201C17 | Warm card |
| Raised | #22252D | #29241E | Warm raised |
| Input | #1F2229 | #241F1A | Warm input |
| Hover | #242833 | #2E2822 | Warm hover |
| Pressed | #2A2E39 | #352E26 | Warm pressed |
| Selected | #2C3240 | #373026 | Warm selected |
| Border | #2D313B | #383126 | Warm border |
| BorderStrong | #3A4050 | #4A4234 | Warm strong |
| BorderSubtle | #242832 | #2C261F | Warm subtle |

### Text — warm white ramp
| Token | Old | New |
|---|---|---|
| Primary | #F5F7FB | #F2EDE4 |
| Secondary | #A3AAB8 | #ABA49A |
| Tertiary | #7C8594 | #7E776C |
| Disabled | #5B6270 | #5E574C |

### Accent — from periwinkle to copper
| Token | Old | New |
|---|---|---|
| Ion (focus/link) | #8FA6FF | #C89060 |
| PrimaryFill | #E9E2D4 (ivory) | #C89060 (copper) |
| PrimaryText | #17181C | #1A1510 |

### Device colors — more saturated, visible
| Device | Old | New |
|---|---|---|
| Headset | #B69CFF | #BCA4F4 |
| Mouse | #F6B24A | #F0B858 |
| Remote | #3DD6C4 | #3CD0B8 |

### Status — fix Light theme mouse/warning collision
- Light Mouse: #9A6A10 (darker, distinct from warning #8C5808 → change warning to #A07008)

## Typography Changes

1. **Remove ALL English uppercase kickers** ("DEVICE OVERVIEW", "HEADSET / SPP", etc.)
2. Replace with clean Chinese subtitle directly under title
3. Keep Segoe UI Variable Display for titles, but increase spacing
4. Battery numbers get device-color, not generic

## Layout Changes

### Device Cards (Hub Page)
- Left edge 4px accent bar (full height) instead of 3px top bar
- Device image plate: 48px (up from 40px), with device tint
- Battery bar uses device color (not generic Ion)
- Remove "进入控制" text link — entire card is already a button
- MinHeight reduced to 280px (less empty space)

### Navigation
- Per-device color indicator on selected item
- Remove "设备"/"系统" headers (use spacing only)
- Cleaner footer

### Page Headers
- Remove eyebrow labels entirely
- Title + subtitle + device-colored status chip only
- More breathing room above cards

### Hub Page Bottom
- Remove "从这里开始" and "读数规则" filler cards
- Replace with a clean system status summary card

## Implementation Order

1. Tokens.Color.xaml (foundation)
2. Cards.xaml (card styles)
3. Navigation.xaml (nav styles)
4. Controls.xaml (button styles)
5. MainWindow.xaml (remove headers, clean footer)
6. DeviceCenterPage.xaml (card redesign, remove filler)
7. HeadsetPage.xaml (remove eyebrow)
8. MousePage.xaml (remove eyebrow)
9. RemotePage.xaml (remove eyebrow)
10. SettingsPage.xaml (remove eyebrow)
