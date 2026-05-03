window.simpleChatScroll = {
    scrollToBottom: function (el) {
        if (!el) return;
        try {
            el.scrollTop = el.scrollHeight;
            // also try parent container if list itself is short
            const parent = el.parentElement;
            if (parent) parent.scrollTop = parent.scrollHeight;
        } catch (e) { /* ignore */ }
    }
};
