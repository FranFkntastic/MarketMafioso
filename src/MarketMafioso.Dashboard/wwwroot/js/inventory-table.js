(() => {
    const storagePrefix = "mmf.inventory.column-widths.";

    function readWidths(table) {
        try {
            return JSON.parse(localStorage.getItem(storagePrefix + table.dataset.inventoryTable) || "{}");
        } catch {
            return {};
        }
    }

    function applyWidths(table) {
        if (!table.dataset.inventoryTable) return;
        const widths = readWidths(table);
        for (const [column, width] of Object.entries(widths)) {
            const header = table.querySelector(`thead tr:first-child th[data-column-id="${CSS.escape(column)}"]`);
            if (header && Number.isFinite(width)) header.style.width = `${width}px`;
        }
    }

    function applyWithin(root) {
        if (root instanceof Element && root.matches("table[data-inventory-table]")) applyWidths(root);
        if (root.querySelectorAll) root.querySelectorAll("table[data-inventory-table]").forEach(applyWidths);
    }

    document.addEventListener("pointerdown", event => {
        const handle = event.target.closest(".inventory-column-resizer");
        if (!handle) return;
        const header = handle.closest("th[data-column-id]");
        const table = handle.closest("table[data-inventory-table]");
        if (!header || !table) return;

        event.preventDefault();
        handle.setPointerCapture(event.pointerId);
        const startX = event.clientX;
        const startWidth = header.getBoundingClientRect().width;
        header.classList.add("resizing");

        const move = moveEvent => {
            const width = Math.max(64, Math.round(startWidth + moveEvent.clientX - startX));
            header.style.width = `${width}px`;
        };
        const finish = () => {
            handle.removeEventListener("pointermove", move);
            handle.removeEventListener("pointerup", finish);
            handle.removeEventListener("pointercancel", finish);
            header.classList.remove("resizing");
            const widths = readWidths(table);
            widths[header.dataset.columnId] = Math.round(header.getBoundingClientRect().width);
            localStorage.setItem(storagePrefix + table.dataset.inventoryTable, JSON.stringify(widths));
        };

        handle.addEventListener("pointermove", move);
        handle.addEventListener("pointerup", finish);
        handle.addEventListener("pointercancel", finish);
    });

    document.addEventListener("dblclick", event => {
        const handle = event.target.closest(".inventory-column-resizer");
        if (!handle) return;
        const header = handle.closest("th[data-column-id]");
        const table = handle.closest("table[data-inventory-table]");
        if (!header || !table) return;
        header.style.removeProperty("width");
        const widths = readWidths(table);
        delete widths[header.dataset.columnId];
        localStorage.setItem(storagePrefix + table.dataset.inventoryTable, JSON.stringify(widths));
    });

    applyWithin(document);
    new MutationObserver(mutations => {
        for (const mutation of mutations)
            for (const node of mutation.addedNodes)
                applyWithin(node);
    }).observe(document.documentElement, { childList: true, subtree: true });
})();
