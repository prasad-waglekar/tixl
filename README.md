## Custom Fork Features

This fork extends **TiXL** with custom rendering and export capabilities designed for deterministic motion graphics and hybrid web-based workflows:

### 🌐 Out-of-Process HTML/CEF Renderer (`feature/htmlrenderer`)
Render live HTML, CSS, JavaScript, and WebGL content directly as textures inside the TiXL node graph via an isolated **Chromium Embedded Framework (CEF)** host (`TixlWebHost`).
- **Out-of-Process Architecture:** Runs the browser engine in a separate host process via named pipes and memory-mapped shared memory ring buffers, keeping CEF decoupled from TiXL's main real-time render loop.
- **Robust Process Isolation:** Enforces manual per-process `RootCachePath` generation to eliminate CEF single-instance locking and prevent `0x80000003` `CHECK()` crashes during multi-instance usage.
- **Deterministic Timeline Sync (`SyncToTiXLTime`):** Injects a clock shim that locks `requestAnimationFrame`, CSS animations, and page timers to TiXL's timeline—ensuring frame-accurate playback when scrubbing or rendering offline.
- **Transparent Overlay Support:** Supports full alpha-channel compositing for layering web UI, kinetic typography, and HUD elements over 3D and shader scenes.

#### Web Modifiers & Operators
| Category | Operator | Description |
| :--- | :--- | :--- |
| **Core** | `Html Renderer` | Manages the browser lifecycle, URL loading, transparency, framerate, and timeline synchronization. |
| **CSS** | `Css Color`, `Css Number`, `Css Property` | Dynamically updates inline styles, CSS custom properties (`--variables`), and values on targeted DOM elements. |
| **DOM** | `Element Text`, `Element Attribute`, `Element Selector`, `Element Visibility` | Modifies text content, HTML/SVG attributes, composite selectors, and element visibility states. |
| **JS** | `Call JavaScript` | Invokes custom JavaScript functions exposed on `window` with JSON-parsed arguments (`window[fn](arg)`). |

### 🎞️ OpenEXR Export (`feature/exr-export`)
- **High-Dynamic-Range Output:** Adds `.exr` image sequence export support for preserving linear color data and full bit-depth in downstream compositing pipelines (e.g., DaVinci Resolve Fusion, Blender, Nuke).

---

## Active Feature Branches
| Branch | Description |
| :--- | :--- |
| [`feature/htmlrenderer`](https://github.com/prasad-waglekar/tixl/tree/feature/htmlrenderer) | `HtmlRenderer` operator, out-of-process CEF host, IPC shared memory, web modifiers, and timeline clock sync |
| [`feature/exr-export`](https://github.com/prasad-waglekar/tixl/tree/feature/exr-export) | Linear HDR OpenEXR image sequence rendering |

# TiXL (Tooll3) - A realtime animation toolkit

[![Discord](https://img.shields.io/discord/823853172619083816.svg?style=for-the-badge)](https://discord.gg/YmSyQdeH3S)
[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg?style=for-the-badge)](https://github.com/tixl3d/tixl/blob/main/LICENSE.txt)

[![tooll-screenshot](https://user-images.githubusercontent.com/1732545/173256422-a4ef9894-d954-4bc3-8c24-000bfbe1c3ad.png)](https://www.youtube.com/watch?v=PrxhwOC9hLw "Tooll3 - A quick overview")

## Overview
TiXL is an open source software to create realtime motion graphics. We are targeting the sweet spot between real-time rendering, graph-based procedural content generation and linear keyframe animation and editing. This combination allows… 
- artists to build audio reactive vj content
- use advanced interfaces for exploring parameters
- or to combine keyframe animation with automation

Technical artists can also dive deeper and use TiXL for advanced development of fragment or compute shaders or to add input from midi controllers and sensors or sources like OSC or Spout. 

We strongly believe in usability and intuitive and beautiful interface design. That's why we experiment with different approaches before striking the right
balance between usability and powerful flexibility. Currently TiXL version 4 is an ongoing development. It's stable enough to produce high-end visuals
create motion graphics use many industry standard features like color correction, scopes and tone mapping, and export small standalone executables.

We hope to bring together a community of artists, developers and creative tinkerers to drive the design and development of the software as a free open source project.

## Installation
Please follow the [installation guide](https://github.com/tixl3d/tixl/wiki/help.Installation).

## Get Help
To get started read the [documentation](https://github.com/tixl3d/tixl/wiki) or watch [tutorial videos](https://www.youtube.com/watch?v=eH2E02U6P5Q&list=PLj-rnPROvbn3LigXGRSDvmLtgTwmNHcQs&index=4).

If you have questions or feedback, please join us on Discord: https://discord.gg/YmSyQdeH3S 



