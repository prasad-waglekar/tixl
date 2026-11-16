## Custom Fork Features (`feature/exr-export`)

This branch extends **TiXL** with linear High-Dynamic-Range (HDR) **OpenEXR (`.exr`)** image sequence rendering for professional post-production and VFX compositing workflows.

### 🎞️ OpenEXR Export (`feature/exr-export`)

Render high-precision linear color and alpha data directly from TiXL's render pipeline into industry-standard `.exr` image sequences.

* **High-Dynamic-Range (HDR) & Linear Color Preservation:** Exports unclipped floating-point pixel values (`16-bit half-float` / `32-bit float`) without tone-mapping loss—ideal for ACEScg and linear compositing pipelines.
* **Lossless Alpha & Compositing Ready:** Preserves clean alpha channels for seamless layering over live-action plates or 3D renders.
* **Post-Production Compatibility:** Designed for direct import into **DaVinci Resolve (Fusion)**, **Blender**, **Nuke**, and **After Effects**.

### Active Feature Branches

| Branch | Description |
| :--- | :--- |
| [`feature/exr-export`](https://github.com/prasad-waglekar/tixl/tree/feature/exr-export) | *(Current Branch)* Linear HDR OpenEXR image sequence rendering |
| [`feature/htmlrenderer`](https://github.com/prasad-waglekar/tixl/tree/feature/htmlrenderer) | `HtmlRenderer` operator, out-of-process CEF host, and deterministic timeline clock sync |

---
# TiXL (Tooll3) - A realtime animation toolkit

[![Discord](https://img.shields.io/discord/823853172619083816.svg?style=for-the-badge)](https://discord.gg/YmSyQdeH3S)
[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg?style=for-the-badge)](https://github.com/tixl3d/tixl/blob/main/LICENSE.txt)
[![Download](https://img.shields.io/badge/Download_TiXL_v4.1.1.1-FF3A9E?style=for-the-badge)](https://github.com/tixl3d/tixl/releases/download/v4.1.1/Tixl-v4.1.1.1.exe)

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



