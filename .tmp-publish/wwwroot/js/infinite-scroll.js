// Infinite scroll helper: watches a sentinel element and invokes a .NET callback
// when it enters the viewport (with a root margin for early triggering).
// Loaded as a classic script (not an ES module) so it works with Blazor WASM's
// IJSRuntime.InvokeAsync("eval", ...) or script tag injection.

(function () {
    let currentObserver = null;

    window.irisInfiniteScroll = {
        attach: function (sentinel, dotNetRef, rootMargin) {
            console.log("[infinite-scroll] attach called", sentinel, rootMargin);
            window.irisInfiniteScroll.detach();
            currentObserver = new IntersectionObserver(
                function (entries) {
                    for (var i = 0; i < entries.length; i++) {
                        if (entries[i].isIntersecting) {
                            dotNetRef.invokeMethodAsync("OnSentinelVisible");
                        }
                    }
                },
                { root: null, rootMargin: rootMargin || "200px" }
            );
            currentObserver.observe(sentinel);
        },
        detach: function () {
            if (currentObserver) {
                currentObserver.disconnect();
                currentObserver = null;
            }
        }
    };
})();
