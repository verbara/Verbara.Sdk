window.HelpInterop = {
    _controller: null,
    registerClickOutside: function (dotNetRef, elementId) {
        this.dispose();
        this._controller = new AbortController();
        document.addEventListener('click', (e) => {
            const el = document.getElementById(elementId);
            if (el && !el.contains(e.target)) {
                dotNetRef.invokeMethodAsync('CloseGlossary');
            }
        }, { signal: this._controller.signal });
    },
    dispose: function () {
        if (this._controller) { this._controller.abort(); this._controller = null; }
    }
    ,
    setAdvanced: function (value) {
        localStorage.setItem('pbxadmin-advanced', value);
    },
    getAdvanced: function () {
        return localStorage.getItem('pbxadmin-advanced') === 'true';
    }
};
