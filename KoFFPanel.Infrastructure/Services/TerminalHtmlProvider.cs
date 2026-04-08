namespace KoFFPanel.Infrastructure.Services;

public static class TerminalHtmlProvider
{
    public static string GetHtml()
    {
        return """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/xterm@5.3.0/css/xterm.css" />
            <style>
                body, html { 
                    margin: 0; padding: 0; height: 100%; 
                    background-color: transparent; /* Делаем фон браузера прозрачным */
                    overflow: hidden; font-family: 'Consolas', monospace;
                }
                
                /* Анимированный фон Авроры */
                .aurora-bg {
                    position: absolute; top: 0; left: 0; width: 100%; height: 100%;
                    background: linear-gradient(45deg, rgba(10,11,15,0.9), rgba(26,28,41,0.85), rgba(15,23,42,0.9));
                    background-size: 400% 400%;
                    animation: aurora 15s ease infinite;
                    z-index: -1;
                }
                
                @keyframes aurora { 
                    0% { background-position: 0% 50%; } 
                    50% { background-position: 100% 50%; } 
                    100% { background-position: 0% 50%; } 
                }
                
                #terminal-container { height: 100%; width: 100%; padding: 10px; box-sizing: border-box; }
                
                /* Кастомизация скроллбара */
                ::-webkit-scrollbar { width: 8px; }
                ::-webkit-scrollbar-track { background: transparent; }
                ::-webkit-scrollbar-thumb { background: #333; border-radius: 4px; }
                ::-webkit-scrollbar-thumb:hover { background: #555; }
            </style>
        </head>
        <body>
            <div class="aurora-bg"></div>
            <div id="terminal-container"></div>
            
            <script src="https://cdn.jsdelivr.net/npm/xterm@5.3.0/lib/xterm.js"></script>
            <script src="https://cdn.jsdelivr.net/npm/xterm-addon-fit@0.8.0/lib/xterm-addon-fit.js"></script>
            <script>
                const term = new Terminal({
                    theme: { background: 'transparent' }, // Терминал прозрачный, чтобы видеть Аврору
                    cursorBlink: true,
                    fontSize: 14,
                    fontFamily: 'Consolas, monospace'
                });
                const fitAddon = new FitAddon.FitAddon();
                term.loadAddon(fitAddon);
                term.open(document.getElementById('terminal-container'));
                fitAddon.fit();

                // Обработка изменения размера окна
                window.addEventListener('resize', () => {
                    fitAddon.fit();
                    // Отправляем новые размеры в C#
                    window.chrome.webview.postMessage(JSON.stringify({ 
                        type: 'resize', 
                        cols: term.cols, 
                        rows: term.rows 
                    }));
                });

                // Отправка ввода пользователя в C#
                term.onData(data => {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'input', data: data }));
                });

                // Функция для приема данных из C# и вывода в терминал
                window.writeToTerminal = function(data) {
                    term.write(data);
                };
            </script>
        </body>
        </html>
        """;
    }
}