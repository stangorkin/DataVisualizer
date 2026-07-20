# DataViz Agent

A local-first, agentic data visualization desktop app powered by a GGUF language model running entirely on your machine — no cloud, no API keys.

Load a CSV, TSV, Parquet, Excel, or JSON file — or connect to a database — then chat with the agent to create and edit charts, answer analytical questions, and explore your data. Files opened on the desktop are **queried in place** by an embedded DuckDB engine rather than loaded into memory, so large datasets (millions of rows) work on ordinary hardware.

---

## Features

- **Conversational chart creation** — describe what you want; the agent picks sensible columns and aggregations
- **Create & update charts** — ask the agent to make a new chart or tweak the selected one
- **Filtered charts & queries** — "sales in 2024 only" or "which region has the highest revenue?"
- **Multi-page reports** — Excel-style page tabs; drag to reorder, double-click or right-click to rename
- **Database import** — connect to SQLite, SQL Server, or PostgreSQL and query directly
- **Runs 100% locally** — uses [LLamaSharp](https://github.com/SciSharp/LLamaSharp) with any GGUF model
- **Print** — File → Print Page (or Ctrl+P)
- **Session save/load** — export and restore your full report + dataset

---

## Quick start (pre-built release)

1. Download `DataVizAgent-vX.Y.Z-win-x64.zip` from the [Releases](../../releases) page.
2. Extract the zip.
3. Run `DataVizAgent.Desktop.exe` — no .NET installation required.
4. On first launch the app detects that no model is installed and offers a one-time download, with picks for every hardware tier (see [Choosing a local model](#choosing-a-local-model)). Downloads are pinned to exact versions and SHA-256-verified.

> **Bring your own model:** you can skip the in-app download entirely — drop any GGUF instruct model into the `models/` folder next to the exe. The app uses the first `*.gguf` it finds, so the filename doesn't matter. Useful for offline machines or models outside the built-in catalog.

### GPU acceleration (optional, build from source)

The pre-built release ships with the **CPU backend only** — setting `"Backend": "cuda"` in it has no effect (the app falls back to CPU). To use an NVIDIA GPU, build from source with the CUDA backend included:

```powershell
dotnet publish src/DataVizAgent.Desktop/DataVizAgent.Desktop.csproj -c Release -p:EnableCuda=true
```

Then edit `appsettings.json` next to the published exe:

```json
"LLamaSharp": {
  "Backend": "cuda",
  "GpuLayerCount": 35
}
```

> The CUDA backend adds roughly 1 GB of native libraries to the output, which is why it is not bundled in the release zip.

---

## Choosing a local model

These four models are the ones the **in-app first-run downloader** offers; this table is your guide for picking one there, or for downloading manually. The app runs any GGUF instruct model, but the agent leans heavily on **tool calling** (it answers by calling chart/query tools, optionally grammar-constrained), and the [Qwen3](https://huggingface.co/collections/Qwen/qwen3) family is a strong default: excellent native tool calling plus a hybrid "thinking" mode. All picks are the `Q4_K_M` quant (the balanced default), linked to [bartowski](https://huggingface.co/bartowski)'s GGUF conversions — to install manually, download one `.gguf` and drop it in `models/`.

| Model | Q4_K_M size | Runs comfortably on | Best for |
|---|---|---|---|
| **[Qwen3-4B](https://huggingface.co/bartowski/Qwen_Qwen3-4B-GGUF)** | ~2.5 GB | 8 GB RAM, CPU-only | Entry tier / any modern laptop with no dedicated GPU. Fastest; handles straightforward "chart sales by region" requests well. |
| **[Qwen3-8B](https://huggingface.co/bartowski/Qwen_Qwen3-8B-GGUF)** | ~5.0 GB | 16 GB RAM, or 8 GB VRAM | **Recommended default.** Best quality-for-size; noticeably better at picking columns, applying filters, and multi-step queries. |
| **[Qwen3-14B](https://huggingface.co/bartowski/Qwen_Qwen3-14B-GGUF)** | ~9.0 GB | 32 GB RAM (slow on CPU), or 12 GB VRAM | Higher-quality reasoning on ambiguous requests. Best with a mid/high-end GPU — slow if run purely on CPU. |
| **[Qwen3-30B-A3B](https://huggingface.co/bartowski/Qwen_Qwen3-30B-A3B-GGUF)** | ~18.6 GB | 32 GB+ RAM, or 24 GB VRAM | Top tier. A mixture-of-experts model with only ~3B *active* parameters, so it runs much faster than its size implies while answering like a far larger model. For workstations / high-VRAM GPUs. |

Notes:

- **Quantization.** `Q4_K_M` is the recommended balance. Each repo also offers smaller quants (`Q3_K_*`, less RAM, some quality loss) and larger ones (`Q5`/`Q6`/`Q8`, better quality, more RAM) — pick another file from the same repo if you want to trade size for quality.
- **RAM vs. VRAM.** CPU-only inference needs roughly the file size in free RAM; GPU offload (build with CUDA — see above — and set `GpuLayerCount`) needs it in VRAM and is much faster. The 4 GB context window adds a few hundred MB of overhead.
- **Thinking mode.** Qwen3 can emit `<think>…</think>` reasoning before its answer. For snappier replies, prompt it to skip thinking, or set `LLamaSharp:ResponseStartMarker` to strip everything up to the end of the reasoning block.
- **Other families work too.** If you prefer Llama, Phi, or Gemma, bartowski publishes GGUF conversions of those as well — just confirm the model is an *instruct/chat* variant with tool-calling support.

---

## Building from source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows (the desktop shell uses WPF + WebView2)
- A GGUF model file (optional — the app offers to download one on first launch)

### Clone and run

```powershell
git clone https://github.com/stangorkin/DataVisualizer.git
cd DataVisualizer
dotnet run --project src/DataVizAgent.Desktop/DataVizAgent.Desktop.csproj
```

By default the app looks for a `models/` folder **next to the built executable** (e.g. `src/DataVizAgent.Desktop/bin/Debug/<tfm>/models/`), or you can let the in-app downloader fetch one on first run.  
Override with the `LLAMASHARP_MODEL_PATH` environment variable, or set `LLamaSharp:ModelPath` in `appsettings.json`.

### Browser dev harness (optional, for development only)

DataViz Agent is a **desktop app** — the WPF shell is the product. The solution also contains a small ASP.NET Core host (`src/DataVizAgent`) that serves the same Blazor UI in a browser. It exists purely as a development convenience: faster iteration on UI changes (no WPF rebuild/file-lock dance) and access to browser devtools. It is not a supported way to run the app.

```powershell
dotnet run --project src/DataVizAgent/DataVizAgent.csproj
```

Then open `http://localhost:5000`.

### Run tests

```powershell
dotnet test tests/DataVizAgent.Core.Tests/DataVizAgent.Core.Tests.csproj
```

---

## Model path resolution

The app resolves the model in this order:

| Priority | Source |
|---|---|
| 1 | `LLamaSharp:ModelPath` in `appsettings.json` (absolute or relative to the exe; a `.gguf` file or a folder containing one) |
| 2 | `LLAMASHARP_MODEL_PATH` environment variable (file or folder, likewise) |
| 3 | First `*.gguf` file found in a `models/` folder next to the exe |

---

## Configuration reference (`appsettings.json`)

| Key | Default | Description |
|---|---|---|
| `LLamaSharp:ModelPath` | `models/` | Path to the GGUF file (see resolution order above) |
| `LLamaSharp:Backend` | `cpu` | `cpu` or `cuda` (`cuda` requires a build with `-p:EnableCuda=true`; falls back to CPU otherwise) |
| `LLamaSharp:Pipeline` | `legacy` | Agent engine: `legacy` (hand-written response parser), `tools` (Microsoft.Extensions.AI function-calling pipeline), or `agent` (Microsoft Agent Framework, with conversation that persists into the session file). See [Agent pipeline](#agent-pipeline-experimental). |
| `LLamaSharp:ConstrainToolCalls` | `true` | `tools` pipeline only: constrain tool-call output with a GBNF grammar generated from the tool schemas, so the model cannot emit a malformed call. Set `false` to compare against prompted-only tool calling. |
| `LLamaSharp:GpuLayerCount` | `0` | Layers to offload to GPU (CUDA only) |
| `LLamaSharp:ContextSize` | `8192` | Model context window in tokens. Larger windows fit wider datasets and longer chats but use more RAM (KV cache grows linearly — roughly an extra ~1 GB per 8k tokens on an 8B model). |
| `LLamaSharp:Temperature` | `0.7` | Sampling temperature |
| `LLamaSharp:MaxTokens` | `1024` | Max tokens per response. Thinking models spend this on hidden reasoning too, so it bounds worst-case reply time on CPU (`-1` = fill the remaining context window). |
| `LLamaSharp:DisableThinking` | `true` | Appends Qwen3's `/no_think` soft switch — skips hidden reasoning, so replies are much faster on CPU and the whole `MaxTokens` budget goes to the visible answer instead of being spent thinking. Harmless on non-Qwen models. Set `false` to let thinking models reason (slower, and long thinking can truncate the answer). |

---

## Agent pipeline (experimental)

The chat agent can run on one of three engines, selected by `LLamaSharp:Pipeline`:

- **`legacy`** (default) — the model emits a fenced ```` ```chart ```` / ```` ```query ```` JSON block which is parsed by hand (`ChartSpecParser` / `DataQueryParser`).
- **`tools`** — the model is driven through the [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai) function-invocation pipeline. The GGUF model is wrapped as an `IChatClient` (`LLamaSharpChatClient`), the chart and query operations are exposed as typed `AIFunction` tools (`ChartTools`), and the framework runs the tool-call loop. This keeps the app provider-agnostic — the same code can later target Ollama, Foundry Local, or a cloud model by swapping the `IChatClient`.
- **`agent`** — wraps the `tools` pipeline in a [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) `ChatClientAgent` (`AgentChatService`). Dataset context is injected each turn by an `AIContextProvider` (`DatasetContextProvider`), and the conversation lives in a serializable agent **session**. After every turn the conversation (the model's session memory plus the rendered chat log) is autosaved to local app data, so the chat — visibly — comes back on the next launch, just like your dataset and report. It is also written into the `.dva-session` file, so explicit Save/Open carries the conversation too.

All three implement the same `IChatService`, so you can A/B them on one model by flipping the flag. The `tools` and `agent` engines use *prompted* tool calling (the tool schema is described to the model, which is asked to emit a `tool_call` block) rather than native tool tokens, since small local models are uneven at the latter.

To make that robust, `LLamaSharp:ConstrainToolCalls` (default `true`) applies **GBNF grammar-constrained decoding**: `GbnfToolGrammarBuilder` turns the tools' JSON schemas into a llama.cpp grammar where the model may emit free prose *or* a single `tool_call` block whose JSON is forced — token by token — to use a known tool name, the right keys, and correctly typed values. A malformed call becomes literally unsamplable. Set it to `false` to compare against prompted-only output. The generator covers flat schemas of string/integer/number/boolean/string-enum (which is what the chart and query tools use); a tool with a nested-object or array parameter falls back to prompted-only automatically.

## Architecture

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full picture — system diagram, the
chart-request flow, the three agent pipelines, the data engine, and the persistence map.

## Project structure

```
src/
  DataVizAgent.Core/       # Models, services, LLamaSharp integration
  DataVizAgent.UI/         # Blazor components (rendered by the desktop shell)
  DataVizAgent/            # ASP.NET Core host — browser dev harness only, not a product surface
  DataVizAgent.Desktop/    # WPF + BlazorWebView desktop app (the product)
tests/
  DataVizAgent.Core.Tests/ # xUnit tests for the core pipeline
```

---

## Releasing

Push a version tag to trigger the GitHub Actions release workflow:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

This builds a self-contained `win-x64` single-file exe, zips it with a `models/` placeholder, and attaches it to a GitHub Release automatically.

---

## License

[MIT](LICENSE)
