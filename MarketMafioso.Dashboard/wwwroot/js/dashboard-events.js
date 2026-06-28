window.marketMafiosoEvents = (() => {
    let controller = null;
    let reconnectTimer = null;
    let generation = 0;

    function stop() {
        generation++;
        if (reconnectTimer) {
            clearTimeout(reconnectTimer);
            reconnectTimer = null;
        }
        if (controller) {
            controller.abort();
            controller = null;
        }
    }

    function start(url, dotNetRef) {
        stop();
        const streamGeneration = ++generation;
        connect(url, dotNetRef, streamGeneration);
    }

    async function connect(url, dotNetRef, streamGeneration) {
        if (streamGeneration !== generation)
            return;

        controller = new AbortController();
        try {
            const response = await fetch(url, {
                cache: "no-store",
                credentials: "same-origin",
                headers: { "Accept": "text/event-stream" },
                signal: controller.signal
            });

            if (!response.ok)
                throw new Error(`stream_http_${response.status}`);
            if (!response.body)
                throw new Error("stream_body_unavailable");

            await dotNetRef.invokeMethodAsync("OnEventStreamOpenAsync");
            await readEvents(response.body, dotNetRef, streamGeneration);

            if (streamGeneration === generation)
                scheduleReconnect(url, dotNetRef, streamGeneration);
        } catch (error) {
            if (streamGeneration !== generation || error?.name === "AbortError")
                return;

            await dotNetRef.invokeMethodAsync("OnEventStreamErrorAsync", error?.message ?? "event_stream_error");
            scheduleReconnect(url, dotNetRef, streamGeneration);
        }
    }

    async function readEvents(body, dotNetRef, streamGeneration) {
        const reader = body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        let eventName = "message";
        let dataLines = [];

        while (streamGeneration === generation) {
            const result = await reader.read();
            if (result.done)
                break;

            buffer += decoder.decode(result.value, { stream: true });
            let lineBreak;
            while ((lineBreak = buffer.indexOf("\n")) >= 0) {
                const rawLine = buffer.slice(0, lineBreak);
                buffer = buffer.slice(lineBreak + 1);
                const line = rawLine.endsWith("\r") ? rawLine.slice(0, -1) : rawLine;

                if (line.length === 0) {
                    await dispatchEvent(eventName, dataLines.join("\n"), dotNetRef);
                    eventName = "message";
                    dataLines = [];
                } else if (line.startsWith("event:")) {
                    eventName = line.slice("event:".length).trimStart();
                } else if (line.startsWith("data:")) {
                    dataLines.push(line.slice("data:".length).trimStart());
                }
            }
        }
    }

    async function dispatchEvent(eventName, data, dotNetRef) {
        if (!data)
            return;

        if (eventName === "acquisition")
            await dotNetRef.invokeMethodAsync("OnAcquisitionEventAsync", data);
    }

    function scheduleReconnect(url, dotNetRef, streamGeneration) {
        if (streamGeneration !== generation)
            return;

        reconnectTimer = setTimeout(() => connect(url, dotNetRef, streamGeneration), 2000);
    }

    return { start, stop };
})();
