# Archived: Hindsight memory engrams

[← Back to Setup & Configuration](../setup.md)

> **This integration is no longer active.** Aria replaced the external Hindsight service with **Noosphere**, a native bridge-local memory system. The instructions below are kept only for historical reference.

---

Aria used to persist and recall memories through the [Hindsight](https://github.com/whoiskatrin/hindsight) REST API directly — **no Python install required**.

1. Install [Docker Desktop](https://www.docker.com/products/docker-desktop/).

   > If you get `docker: error getting credentials - err: exec: "docker-credential-desktop": executable file not found in $PATH`, edit `~/.docker/config.json` and change `credsStore` to `credStore` (drop the S).

2. Set the LLM provider variables Hindsight uses for memory extraction:

   ```bash
   # Remote machine or cloud API
   export HINDSIGHT_API_LLM_PROVIDER=openai
   export HINDSIGHT_API_LLM_BASE_URL=http://<machine-ip>:1234/v1
   export HINDSIGHT_API_LLM_API_KEY="$(cat <path-to-api-key-file>)"
   export HINDSIGHT_API_LLM_MODEL=<model-name>
   ```

   ```bash
   # LM Studio running locally (use host.docker.internal, NOT localhost — Docker can't reach the host via localhost)
   export HINDSIGHT_API_LLM_PROVIDER=lmstudio
   export HINDSIGHT_API_LLM_BASE_URL=http://host.docker.internal:1234/v1
   export HINDSIGHT_API_LLM_MODEL=<model-name>
   ```

   > ⚠️ LM Studio's OpenAI-compatible endpoint is `/v1`, not `/api/v1`. Using `/api/v1` returns 404s.

3. **Use the patched image for local reasoning backends.** It fixes LLamaCPP/LM Studio/Ollama compatibility (reasoning models like Qwen 3). It also adds CORS headers, but that's no longer required by Aria — Hindsight is now reached through the cogitator node, not a browser fetch (see note below). If your memory-extraction LLM is a normal cloud/OpenAI endpoint, the stock image is fine.

   ```bash
   # Build once, from the repo root
   docker build -f ./aria-agent/hindsight-custom/dockerfile -t hindsight-patched .

   # Run
   docker run --rm -it -p 8888:8888 -p 9999:9999 \
       -e HINDSIGHT_API_LLM_PROVIDER=$HINDSIGHT_API_LLM_PROVIDER \
       -e HINDSIGHT_API_LLM_BASE_URL=$HINDSIGHT_API_LLM_BASE_URL \
       -e HINDSIGHT_API_LLM_API_KEY=$HINDSIGHT_API_LLM_API_KEY \
       -e HINDSIGHT_API_LLM_MODEL=$HINDSIGHT_API_LLM_MODEL \
       -v $HOME/.hindsight-docker:/home/hindsight/.pg0 \
       hindsight-patched:latest
   ```

   Startup confirms the CORS patch is active:
   ```
   INFO - hindsight_api.api - [CORS-PATCH] CORSMiddleware injected — CORS enabled for all origins
   ```

4. Open the [Hindsight Dashboard](http://localhost:9999/dashboard), create a **Memory Bank**, and name it `Aria.Agent` (or anything — set the name in the Web UI or `appsettings.json`).

**Configure Aria to use it.** In **Aria.Web**, enter the Base URL and Bank ID in the Hindsight tool modal (⚙). In **Aria.Console**:

```json
"Hindsight": {
  "BaseURL": "http://localhost:8888",
  "BankID": "Aria.Agent"
}
```

In the Web UI, Hindsight calls route through your cogitator node (the node fetches `localhost:8888`, not the browser) — so **no CORS is required** on Hindsight. Large recall responses are auto-split into chunks to stay within SignalR limits.
