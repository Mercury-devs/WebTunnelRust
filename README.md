# WebTunnel - туннель для веб-запросов RUST + C# API

**WebTunnel** - это связка:

- плагин для RUST (Oxide/Carbon), который перехватывает HTTP-запросы (`webrequest.Enqueue`)  
  и при необходимости отправляет их во внешний туннель;
- C#-сервис (API на .NET), который принимает эти запросы и пересылает их на реальные адреса  
  (Discord, любые внешние API, ваши сервисы и т.п.).

Это позволяет:
- прятать реальные webhook/API-адреса за своим VDS;
- логировать и контролировать исходящие запросы с RUST-сервера;
- фильтровать, какие запросы гонять через туннель, а какие - отправлять напрямую.

---

## Архитектура

Схема работы:

1. Любой плагин вызывает, например:

   ```csharp
   webrequest.Enqueue("https://discord.com/api/...", body, callback, this, RequestMethod.POST);
   ```

2. Плагин **WebTunnel** через Harmony патчит `WebRequests.Enqueue` и:

   - проверяет URL (http/https, белый список и т.п.);
   - если запрос нужно туннелировать:
     - сохраняет оригинальный `url` как `target`;
     - подменяет `url` на адрес туннеля:  
       `http://ВАШ_VDS:5000/tunnel/forward`;
     - заворачивает тело в JSON:

       ```json
       {
         "target": "https://discord.com/api/...",
         "method": "POST",
         "plugin": "ИмяПлагина",
         "body": "{...оригинальное тело...}"
       }
       ```
     - добавляет заголовок `X-Tunnel-Secret` с секретным ключом.

3. C#-API (туннель) на вашем VDS:

   - проверяет `X-Tunnel-Secret`;
   - логирует входящий JSON;
   - делает реальный HTTP-запрос на `target`;

Для других плагинов это выглядит так, как будто они по-прежнему ходят напрямую.

---

## Состав репозитория

- `WebTunnel.cs` - плагин для RUST (Oxide/Carbon).
- `WTR` - C#-API-туннель на .NET 8.
  - `Program.cs` - основной код туннеля.
  - `appsettings.json` - конфиг (секретный ключ и т.п.)

---

## Требования

### RUST-сервер

- Oxide / Carbon
- Поддержка Harmony (используется атрибут `[AutoPatch]` и `HarmonyPatch`).

### Туннель (VDS / Linux)

- Linux x64 (Ubuntu/Debian/и т.п.)
- Установленный .NET 8 Runtime / ASP.NET Core Runtime **или** self-contained сборка без рантайма.
- Открытый порт, по умолчанию `5000` (или любой свой).

---

## Установка и настройка C# API-туннеля на VDS

### 1. Подготовка VDS (Ubuntu пример)

Обновляем систему:

```bash
apt update
apt upgrade -y
```

Ставим nano : 

```bash
apt install -y nano
```

### 2. Установка .NET Runtime :

Пример (Ubuntu, .NET 8):

```bash
apt install -y dotnet-runtime-8.0 aspnetcore-runtime-8.0
```

### 3. Копирование файлов на VDS

Создаём папку для туннеля:

```bash
mkdir -p /opt/wtr
```
Загружаем все содержимое архива в эту папку.

Проверяем на сервере:

```bash
ls /opt/wtr
```

Должно быть:

```text
WTR.dll
WTR.deps.json
WTR.runtimeconfig.json
appsettings.json
...
```

### 5. Настройка секрета (appsettings.json)

Открываем `/opt/wtr/appsettings.json`:

```json
{
  "Tunnel": {
    "SharedSecret": "super-secret-token"
  }
}
```
Устанавливаем свой секретный ключ.
Секрет `"super-secret-token"` должен совпадать с тем, что будет в конфиге плагина RUST.

### 6. Запуск туннеля как systemd-сервис

Создаём unit-файл:

```bash
nano /etc/systemd/system/wtr.service
```

Содержимое:

```ini
[Unit]
Description=WTR Web Tunnel
After=network.target

[Service]
WorkingDirectory=/opt/wtr
ExecStart=/usr/bin/dotnet /opt/wtr/WTR.dll
Restart=always
RestartSec=5
User=root
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

Перезагружаем конфигурацию systemd и запускаем сервис:

```bash
systemctl daemon-reload
systemctl start wtr.service
systemctl enable wtr.service
systemctl status wtr.service
```

Логи туннеля в реальном времени:

```bash
journalctl -u wtr.service -f
```
---

## Установка и настройка плагина WebTunnel.cs

### 1. Установка плагина

1. Поместите файл `WebTunnel.cs` в папку:

   ```text
   oxide/plugins/WebTunnel.cs
   ```

2. Перезапустите сервер RUST или в консоли:

   ```text
   oxide.reload WebTunnel
   ```

### 2. Конфигурация плагина

После первого запуска создаётся конфиг:

`oxide/config/WebTunnel.json`

Пример:

```json
{
  "Секретный ключ - как в указан в WTR-API": "super-secret-token",
  "Укажите ссылку на ваш сайт с туннелем": "https://mysupersite.com",
  "Список адресов для туннелирования [Если пустой - будут отправляться в туннель все запросы]": [
    "discord.com",
    "discordapp.com",
    "discord"
  ]
}
```

#### Поля

- **`Секретный ключ - как в указан в WTR-API`**  
  Должен совпадать с `Tunnel:SharedSecret` в `appsettings.json` туннеля.

- **`Укажите ссылку на ваш сайт с туннелем (сервер где установлен WTR)`**  
  Примеры:

  - `http://127.0.0.1:5000`
  - `http://mysupersite:5000`

- **`Список адресов для туннелирования [...]`**  

  Логика:

  - если список **пустой** → в туннель идут **все** http/https-запросы;
  - если список **не пустой** → в туннель идут только те запросы, чей URL/host совпадает с одним из элементов списка.

  Поддерживаются варианты:

  - полный URL (`https://myapi.example.com`),
  - домен (`discord.com`),
  - поддомен (`api.steampowered.com`),
  - фрагмент (`discord`).

Примеры значений:

```json
".....": [
  "discord.com",
  "discordapp.com",
  "api.steampowered.com",
  "myapi.example.com"
]
```

## Логи

### Логи туннеля (на VDS)

```bash
journalctl -u wtr.service -f
```

Пример вывода:

```text
========== NEW TUNNEL REQUEST ==========
[Tunnel] Получен JSON:
{"target":"https://discord.com/api/webhooks/...","method":"POST","plugin":"IQChat","body":"..."}
[Tunnel] Получен запрос из плагина: IQChat
[Tunnel] Перенаправление: POST https://discord.com/api/webhooks/...
[Tunnel] Сервер-назначение ответил: 204 (NoContent)
[Tunnel] Тело ответа (первые 200 символов):

========================================
```

### Логи плагина (Rust-сервер)

В консоли / логах сервера:

```text
[WebTunnel] Отправлено в туннель : https://discord.com/api/webhooks/...
[WebTunnel] URL туннеля: http://95.81.120.52:5000/tunnel/forward
```

---
