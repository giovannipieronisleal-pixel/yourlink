(() => {
    window.youlinkStore = {
        async copyText(text) {
            if (!text) return false;
            try {
                if (navigator.clipboard && window.isSecureContext) {
                    await navigator.clipboard.writeText(text);
                    return true;
                }
            } catch {
                // Continua para fallback compatível com navegadores/webviews mais restritos.
            }

            try {
                const ta = document.createElement("textarea");
                ta.value = text;
                ta.setAttribute("readonly", "");
                ta.style.position = "fixed";
                ta.style.top = "-1000px";
                ta.style.left = "-1000px";
                document.body.appendChild(ta);
                ta.focus();
                ta.select();
                const ok = document.execCommand("copy");
                document.body.removeChild(ta);
                return !!ok;
            } catch {
                return false;
            }
        },

        async shareOrCopy(title, text, url) {
            const targetUrl = url || window.location.href;
            if (navigator.share) {
                try {
                    await navigator.share({
                        title: title || document.title,
                        text: text || title || "",
                        url: targetUrl
                    });
                    return "shared";
                } catch (err) {
                    if (err && err.name === "AbortError") {
                        return "cancelled";
                    }
                }
            }

            const copied = await this.copyText(targetUrl);
            return copied ? "copied" : "failed";
        },

        getCart(storageKey) {
            try {
                return window.localStorage.getItem(storageKey) || "[]";
            } catch {
                return "[]";
            }
        },

        saveCart(storageKey, json) {
            try {
                window.localStorage.setItem(storageKey, json || "[]");
                return true;
            } catch {
                return false;
            }
        }
    };
})();
