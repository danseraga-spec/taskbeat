# Отзывы и баг-репорты → Telegram

Окно «Отзыв или баг» в Taskbeat отправляет текст и скриншот на маленький посредник — Cloudflare Worker (бесплатно). Посредник пересылает их в ваш Telegram-бот.

Токен бота хранится только в секретах Cloudflare. В приложении и в репозитории его нет.

```
Taskbeat  ──POST /feedback──▶  Cloudflare Worker  ──Bot API──▶  ваш чат в Telegram
```

Посредник:
- принимает не больше 5 отзывов в час с одного адреса;
- обрезает текст до 3500 символов;
- принимает только PNG и JPEG до 8 МБ.

## 1. Бот

1. Напишите [@BotFather](https://t.me/BotFather) → `/newbot` и сохраните **токен**.
2. Откройте своего бота и нажмите **Start**.
3. Узнайте **chat id**: откройте в браузере `https://api.telegram.org/bot<ТОКЕН>/getUpdates` и найдите `"chat":{"id": …}`. Это ваш chat id.

   Чтобы отзывы приходили в группу, добавьте туда бота и возьмите id группы: он начинается с `-100`.

## 2. Посредник

**Вариант А — через сайт, без установки:**

1. Зарегистрируйтесь на [dash.cloudflare.com](https://dash.cloudflare.com) (бесплатный план).
2. **Workers & Pages → Create → Create Worker**. Назовите воркер `taskbeat-feedback` → **Deploy**.
3. **Edit code**: вставьте содержимое [`worker.js`](worker.js) → **Deploy**.
4. **Settings → Variables and Secrets**: добавьте два секрета (тип **Secret**):
   - `BOT_TOKEN` — токен бота;
   - `CHAT_ID` — chat id.
5. Адрес воркера будет вида `https://taskbeat-feedback.<ваше-имя>.workers.dev`.

**Вариант Б — из консоли (нужен Node.js):**

```bat
cd relay
npx wrangler login
npx wrangler deploy
npx wrangler secret put BOT_TOKEN
npx wrangler secret put CHAT_ID
```

## 3. Адрес в приложении

В [`app/Hub/FeedbackWindow.cs`](../app/Hub/FeedbackWindow.cs) укажите адрес с `/feedback` на конце:

```csharp
public const string Endpoint = "https://taskbeat-feedback.<ваше-имя>.workers.dev/feedback";
```

Пересоберите (`build.cmd`). Пока адрес пустой, кнопка «Отправить» в форме неактивна.

## Проверка

```bat
curl -X POST https://taskbeat-feedback.<ваше-имя>.workers.dev/feedback -H "X-Taskbeat: 1" -F kind=bug -F "text=проверка связи"
```

Ответ `ok` — и в Telegram приходит сообщение.
