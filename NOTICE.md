# Third-party notices

Dwight is Apache-2.0 (see LICENSE). It builds on, embeds or runs alongside the following.

## Embedded or linked in the plugin

| Component | Version | Licence | Use |
| --- | --- | --- | --- |
| Microsoft Automatic Graph Layout (Microsoft.Msagl) | 1.1.6 | MIT | Layout engine for Reorganise |
| Markdig | 0.37.0 | BSD-2-Clause | Markdown to HTML for results |
| GhJSON.Core, GhJSON.Grasshopper | 1.0.0 | Apache-2.0 | Reading Grasshopper documents into a serialisable form |
| JsonSchema.Net | 7.2.2 | MIT | Pulled in by GhJSON.Core |
| JsonPointer.Net | 5.0.0 | MIT | Pulled in by JsonSchema.Net, for GhJSON.Core |
| Json.More.Net | 2.0.1.2 | MIT | Pulled in by JsonPointer.Net, for GhJSON.Core |
| Humanizer (Humanizer.Core) | 2.14.1 | MIT | Pulled in by JsonPointer.Net, for GhJSON.Core |
| Costura.Fody and Fody (build time) | 5.7.0 / 6.8.2 | MIT (Costura.Fody); Fody is a build-time tool and is not redistributed | Embeds dependency assemblies into the .gha |
| System.Text.Json | 8.0.5 | MIT | JSON |
| System.Text.Encodings.Web | 8.0.0 | MIT | Pulled in by System.Text.Json |
| Newtonsoft.Json | 13.0.3 | MIT | Embedded via Costura |
| Eto.Forms | 2.11.0 | BSD-3-Clause | Native fallback surfaces (provided by Rhino at runtime, not redistributed) |

## Icons (embedded)

Tabler Icons 3.50.0, outline set, MIT. Copyright (c) 2020-2026 Paweł Kuna. The full text is in `docs/public/licenses/tabler-icons-LICENSE.txt`, and the icons used are listed in `tools/build-icon-sprite.js`.

## Fonts (embedded, SIL Open Font Licence 1.1)

Space Grotesk, Inter, Space Mono. Each licence text, with its copyright line, is in `docs/public/licenses/`: `OFL-space-grotesk.txt`, `OFL-inter.txt` and `OFL-space-mono.txt`.

## Used but not redistributed

- RhinoCommon and the Grasshopper SDK, copyright Robert McNeel & Associates, used under McNeel's licence. Dwight is built against them and does not ship them.
- Microsoft WebView2 runtime, supplied by Windows or Rhino, used for the panels.
- Model APIs from Anthropic, OpenAI, Google and Ollama, called with the user's own key. Dwight has no server of its own.
