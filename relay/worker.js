// Taskbeat — посредник для отзывов и баг-репортов.
// Принимает форму из приложения и пересылает её в Telegram-бота.
// Токен бота хранится только здесь, в секретах Cloudflare (BOT_TOKEN, CHAT_ID), — в приложении его нет.
//
// Запрос: POST /feedback, multipart/form-data
//   kind     — bug | idea | review
//   text     — текст (до 3500 символов)
//   contact  — как связаться (необязательно)
//   app      — версия и Windows (необязательно)
//   image    — скриншот PNG/JPEG (необязательно, до 8 МБ)

const MAX_TEXT = 3500;
const MAX_IMAGE = 8 * 1024 * 1024;
const PER_HOUR = 5; // отзывов в час с одного адреса

const hits = new Map(); // простой лимит в памяти воркера: ip -> [время...]

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method !== "POST" || url.pathname !== "/feedback") return reply(404, "not found");
    if (!env.BOT_TOKEN || !env.CHAT_ID) return reply(500, "relay is not configured");
    if (request.headers.get("X-Taskbeat") !== "1") return reply(400, "bad client");

    const ip = request.headers.get("CF-Connecting-IP") || "?";
    if (limited(ip)) return reply(429, "too many requests");

    let form;
    try { form = await request.formData(); } catch { return reply(400, "bad form"); }

    const kind = { bug: "🐞 Баг", idea: "💡 Идея", review: "💬 Отзыв" }[form.get("kind")] || "💬 Отзыв";
    const text = clean(form.get("text"), MAX_TEXT);
    if (text.length < 3) return reply(400, "empty text");
    const contact = clean(form.get("contact"), 200);
    const app = clean(form.get("app"), 200);

    const message =
      `<b>${kind}</b>\n\n${esc(text)}` +
      (contact ? `\n\n<b>Контакт:</b> ${esc(contact)}` : "") +
      (app ? `\n<i>${esc(app)}</i>` : "");

    const image = form.get("image");
    const api = `https://api.telegram.org/bot${env.BOT_TOKEN}`;
    let res;
    if (image && typeof image !== "string" && image.size > 0) {
      if (image.size > MAX_IMAGE) return reply(413, "image too large");
      if (!/^image\/(png|jpeg)$/.test(image.type)) return reply(415, "image must be png or jpeg");
      // подпись к фото — до 1024 символов; длинный текст отправляем отдельным сообщением
      if (message.length > 1000) {
        res = await tg(`${api}/sendMessage`, json({ chat_id: env.CHAT_ID, text: message, parse_mode: "HTML" }));
        if (!res.ok) return reply(502, "telegram error");
      }
      const fd = new FormData();
      fd.append("chat_id", env.CHAT_ID);
      fd.append("photo", image, image.name || "screenshot.jpg");
      if (message.length <= 1000) { fd.append("caption", message); fd.append("parse_mode", "HTML"); }
      res = await tg(`${api}/sendPhoto`, { method: "POST", body: fd });
      // слишком большое для «фото» — отправляем файлом
      if (!res.ok) {
        const fd2 = new FormData();
        fd2.append("chat_id", env.CHAT_ID);
        fd2.append("document", image, image.name || "screenshot.png");
        if (message.length <= 1000) { fd2.append("caption", message); fd2.append("parse_mode", "HTML"); }
        res = await tg(`${api}/sendDocument`, { method: "POST", body: fd2 });
      }
    } else {
      res = await tg(`${api}/sendMessage`, json({ chat_id: env.CHAT_ID, text: message, parse_mode: "HTML" }));
    }
    return res.ok ? reply(200, "ok") : reply(502, "telegram error");
  },
};

function limited(ip) {
  const now = Date.now();
  const list = (hits.get(ip) || []).filter((t) => now - t < 3600_000);
  if (list.length >= PER_HOUR) { hits.set(ip, list); return true; }
  list.push(now);
  hits.set(ip, list);
  if (hits.size > 5000) hits.clear();
  return false;
}

const clean = (v, max) => (typeof v === "string" ? v.trim().slice(0, max) : "");
const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
const json = (o) => ({ method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(o) });
const tg = (u, init) => fetch(u, init);
const reply = (status, text) => new Response(text, { status, headers: { "content-type": "text/plain; charset=utf-8" } });
