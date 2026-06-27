window.marketMafiosoEvents = (() => {
    let source = null;

    function stop() {
        if (source) {
            source.close();
            source = null;
        }
    }

    function start(url, dotNetRef) {
        stop();
        source = new EventSource(url, { withCredentials: true });
        source.addEventListener("acquisition", event => {
            dotNetRef.invokeMethodAsync("OnAcquisitionEventAsync", event.data);
        });
        source.onerror = () => {
            dotNetRef.invokeMethodAsync("OnEventStreamErrorAsync", "event_stream_error");
        };
    }

    return { start, stop };
})();
