// Captures raw microphone PCM for on-device Whisper. Runs on the audio-render thread (reliable,
// unlike the deprecated ScriptProcessorNode) and posts each Float32 frame to the main thread.
// It produces no output, so wiring it to the destination is silent (no speaker feedback).
class VoxRecorderProcessor extends AudioWorkletProcessor {
    process(inputs) {
        const input = inputs[0];
        if (input && input[0] && input[0].length) {
            // Copy — the render thread reuses the buffer after process() returns.
            this.port.postMessage(input[0].slice(0));
        }
        return true; // keep processor alive until the node is disconnected
    }
}
registerProcessor('vox-recorder', VoxRecorderProcessor);
