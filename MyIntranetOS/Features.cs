using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SQLite;

namespace MyIntranetOS
{
    // ----------------------------------------------------------------------
    //  Tables SQLite des applications intégrées
    // ----------------------------------------------------------------------

    [Table("notifications")]
    public class NotificationEntry
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed] public int CharacterId { get; set; }
        public string AppId { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public string Type { get; set; }
        public bool Read { get; set; }
        public long CreatedAt { get; set; }
    }

    [Table("bank_history")]
    public class BankEntry
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed] public int CharacterId { get; set; }
        public double Amount { get; set; }
        public string Reason { get; set; }
        public long CreatedAt { get; set; }
    }

    [Table("bank_pending")]
    public class PendingCredit
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed] public int CharacterId { get; set; }
        public double Amount { get; set; }
        public string Reason { get; set; }
        public long CreatedAt { get; set; }
    }

    [Table("news")]
    public class NewsArticle
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string Outlet { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
        public long CreatedAt { get; set; }
    }

    [Table("birdy_posts")]
    public class BirdyPost
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed] public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string Handle { get; set; }
        public string Text { get; set; }
        public int Likes { get; set; }
        public long CreatedAt { get; set; }
    }

    [Table("birdy_likes")]
    public class BirdyLike
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed] public int PostId { get; set; }
        [Indexed] public int CharacterId { get; set; }
    }

    [Table("ads")]
    public class Ad
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed] public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string Phone { get; set; }
        public string Category { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public double Price { get; set; }
        public long CreatedAt { get; set; }
        public long ExpiresAt { get; set; }
    }

    [Table("service_requests")]
    public class ServiceRequest
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed] public string ServiceId { get; set; }
        [Indexed] public int RequesterId { get; set; }
        public string RequesterName { get; set; }
        public string RequesterPhone { get; set; }
        public string Message { get; set; }
        public bool HasPosition { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public string Status { get; set; }
        public int AcceptedById { get; set; }
        public string AcceptedByName { get; set; }
        public string AcceptedByPhone { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
    }

    public class ServiceConfig
    {
        public string Id;
        public string Name;
        public string[] Activities = new string[0];
        public int[] BizIds = new int[0];
        public bool RequireOnDuty = true;
        public string RequestLabel = "Demander";
        public string Placeholder = "Votre demande…";
    }

    public class RadioStation
    {
        public string Name;
        public string Url;
    }

    internal sealed class ApiContext
    {
        public HttpRequest Req;
        public string Path;
        public string Method => Req.Method;
        public Session Session;
        public PlayerSnapshot Player;
        public TabletProfile Profile;
        public int CharacterId => Session.CharacterId;

        private JObject body;
        public JObject Body
        {
            get
            {
                if (body != null) return body;
                try { body = Req.Body.Length == 0 ? new JObject() : JObject.Parse(Req.BodyText); }
                catch { body = new JObject(); }
                return body;
            }
        }

        public string Str(string key, int max)
        {
            string v = Body[key]?.Type == JTokenType.String ? (string)Body[key] : Body[key]?.ToString();
            v = v?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            return v.Length > max ? v.Substring(0, max) : v;
        }
    }

    internal sealed class ApiError : Exception
    {
        public readonly int Status;
        public ApiError(int status, string message) : base(message) { Status = status; }
    }

    /// <summary>
    /// API des applications intégrées, reliées au jeu : notifications, banque, SMS, contacts, mail, actualités,
    /// réseau social, véhicules, immobilier, petites annonces, services (taxi, santé...) et radio.
    /// </summary>
    internal sealed class Features
    {
        private static readonly Regex PhonePattern = new Regex("^[0-9 +.-]{3,20}$");

        private readonly MyIntranetPlugin plugin;
        private readonly IGameBridge game;
        private readonly ConcurrentDictionary<string, long> rateLimits = new ConcurrentDictionary<string, long>();

        public Features(MyIntranetPlugin plugin, IGameBridge game)
        {
            this.plugin = plugin;
            this.game = game;
            plugin.Db.Register<NotificationEntry>();
            plugin.Db.Register<BankEntry>();
            plugin.Db.Register<PendingCredit>();
            plugin.Db.Register<NewsArticle>();
            plugin.Db.Register<BirdyPost>();
            plugin.Db.Register<BirdyLike>();
            plugin.Db.Register<Ad>();
            plugin.Db.Register<ServiceRequest>();
        }

        private IntranetConfig Config => plugin.Config;
        private Database Db => plugin.Db;

        // ------------------------------------------------------------------
        //  Point d'entrée
        // ------------------------------------------------------------------

        public HttpResponse Handle(ApiContext c)
        {
            string[] p = c.Path.Trim('/').Split('/');
            try
            {
                switch (p[0])
                {
                    case "notifications": return Notifications(c, p);
                    case "wallet": return Get(c, () => Wallet(c));
                    case "bank": return Bank(c, p);
                    case "sms": return Sms(c, p);
                    case "contacts": return Contacts(c, p);
                    case "directory": return Get(c, () => game.GetBizs().Select(b => new
                    {
                        id = b.Id, name = b.Name, description = b.Description, contact = b.Contact,
                        recruiting = b.Recruiting, activities = b.Activities
                    }).OrderBy(b => b.name).ToList());
                    case "mail": return Mail(c, p);
                    case "news": return News(c, p);
                    case "birdy": return Birdy(c, p);
                    case "vehicles": return Vehicles(c, p);
                    case "realestate": return RealEstate(c, p);
                    case "ads": return Ads(c, p);
                    case "services": return Services(c, p);
                    case "radio": return Get(c, () => (Config.RadioStations ?? new List<RadioStation>()).Select(r => new { name = r.Name, url = r.Url }));
                    default: return null;
                }
            }
            catch (ApiError e)
            {
                return HttpResponse.Error(e.Status, "error", e.Message);
            }
            catch (TimeoutException e)
            {
                return HttpResponse.Error(503, "game_unavailable", e.Message);
            }
            catch (AggregateException e) when (e.InnerException is TimeoutException)
            {
                return HttpResponse.Error(503, "game_unavailable", e.InnerException.Message);
            }
        }

        private static HttpResponse Get(ApiContext c, Func<object> f)
        {
            if (c.Method != "GET") throw new ApiError(405, "Méthode non autorisée.");
            return HttpResponse.Json(f());
        }

        private static HttpResponse Ok(object value = null) => HttpResponse.Json(value ?? new { ok = true });

        private void RateLimit(ApiContext c, string key, int seconds)
        {
            string k = $"{c.CharacterId}:{key}";
            long now = Clock.Now;
            if (rateLimits.TryGetValue(k, out long last) && now - last < seconds)
                throw new ApiError(429, $"Patientez {seconds - (now - last)} s avant de recommencer.");
            rateLimits[k] = now;
        }

        // ------------------------------------------------------------------
        //  Notifications
        // ------------------------------------------------------------------

        /// <summary>Enregistre une notification pour un personnage (visible sur la tablette) et l'affiche en jeu s'il est connecté.</summary>
        public void Push(int characterId, string appId, string title, string message, string type = "info", bool inGame = true)
        {
            Db.With(db => db.Insert(new NotificationEntry
            {
                CharacterId = characterId, AppId = appId, Title = Trim(title, 80), Message = Trim(message, 300),
                Type = type, CreatedAt = Clock.Now
            }));
            if (inGame) game.Notify(characterId, title, message, type);
        }

        private HttpResponse Notifications(ApiContext c, string[] p)
        {
            int me = c.CharacterId;
            if (p.Length == 1 && c.Method == "GET")
            {
                List<NotificationEntry> list = Db.With(db => db.Table<NotificationEntry>()
                    .Where(n => n.CharacterId == me).OrderByDescending(n => n.Id).Take(50).ToList());
                int unread = Db.With(db => db.Table<NotificationEntry>().Where(n => n.CharacterId == me && !n.Read).Count());
                return HttpResponse.Json(new
                {
                    unread,
                    items = list.Select(n => new { id = n.Id, app = n.AppId, title = n.Title, message = n.Message, type = n.Type, read = n.Read, createdAt = n.CreatedAt })
                });
            }
            if (p.Length == 2 && p[1] == "read" && c.Method == "POST")
            {
                List<int> ids = c.Body["ids"]?.ToObject<List<int>>();
                Db.With(db =>
                {
                    foreach (NotificationEntry n in db.Table<NotificationEntry>().Where(n => n.CharacterId == me && !n.Read).ToList())
                    {
                        if (ids != null && !ids.Contains(n.Id)) continue;
                        n.Read = true;
                        db.Update(n);
                    }
                    return 0;
                });
                return Ok();
            }
            if (p.Length == 2 && p[1] == "clear" && c.Method == "POST")
            {
                Db.With(db => db.Execute("DELETE FROM notifications WHERE CharacterId = ?", me));
                return Ok();
            }
            return null;
        }

        // ------------------------------------------------------------------
        //  Portefeuille et banque
        // ------------------------------------------------------------------

        private object Wallet(ApiContext c)
        {
            PlayerSnapshot s = c.Player;
            return new
            {
                fullName = s.FullName,
                firstName = s.FirstName,
                lastName = s.LastName,
                birthday = s.Birthday,
                commune = s.Commune,
                height = s.Height,
                sex = s.SexId,
                idCard = s.IdCard,
                phoneNumber = s.PhoneNumber,
                money = Config.ShowMoney ? s.Money : (double?)null,
                bank = Config.ShowMoney ? s.Bank : (double?)null,
                permisB = s.PermisB,
                permisPoints = s.PermisPoints,
                hasBankCard = s.HasBCR,
                biz = s.BizId > 0 ? new { id = s.BizId, name = s.BizName } : null,
                level = s.Level
            };
        }

        /// <summary>Appelé à chaque mouvement bancaire du jeu (thread principal).</summary>
        public void LogBank(int characterId, double amount, string reason)
        {
            Db.With(db => db.Insert(new BankEntry
            {
                CharacterId = characterId, Amount = Math.Round(amount, 2), Reason = string.IsNullOrWhiteSpace(reason) ? "Opération" : Trim(reason, 120), CreatedAt = Clock.Now
            }));
        }

        /// <summary>Virements reçus pendant que le joueur était hors ligne (appelé à son arrivée, thread principal).</summary>
        public List<PendingCredit> TakePendingCredits(int characterId) => Db.With(db =>
        {
            List<PendingCredit> list = db.Table<PendingCredit>().Where(x => x.CharacterId == characterId).ToList();
            foreach (PendingCredit x in list) db.Delete<PendingCredit>(x.Id);
            return list;
        });

        private HttpResponse Bank(ApiContext c, string[] p)
        {
            int me = c.CharacterId;
            if (p.Length == 1 && c.Method == "GET")
            {
                double? bank = game.GetBank(me) ?? c.Player.Bank;
                List<BankEntry> history = Db.With(db => db.Table<BankEntry>().Where(x => x.CharacterId == me).OrderByDescending(x => x.Id).Take(60).ToList());
                return HttpResponse.Json(new
                {
                    bank = Config.ShowMoney ? bank : null,
                    money = Config.ShowMoney ? c.Player.Money : (double?)null,
                    iban = $"FR76 {me:D4} {c.Player.SteamId % 10000:D4} AMB",
                    maxTransfer = Config.BankTransferMax,
                    history = history.Select(h => new { id = h.Id, amount = h.Amount, reason = h.Reason, createdAt = h.CreatedAt })
                });
            }
            if (p.Length == 2 && p[1] == "transfer" && c.Method == "POST")
            {
                if (!Config.BankTransfersEnabled) throw new ApiError(403, "Les virements sont désactivés sur ce serveur.");
                string to = c.Str("to", 60);
                string label = c.Str("label", 60) ?? "Virement";
                double amount = Math.Round(c.Body["amount"]?.Value<double?>() ?? 0, 2);
                if (to == null) throw new ApiError(400, "Indiquez un numéro de téléphone ou un nom.");
                if (amount <= 0) throw new ApiError(400, "Montant invalide.");
                if (amount > Config.BankTransferMax) throw new ApiError(400, $"Montant maximum : {Config.BankTransferMax:N0} €.");

                CharacterRef target = FindCharacter(to);
                if (target == null) throw new ApiError(404, "Destinataire introuvable (numéro ou « Prénom Nom »).");
                if (target.Id == me) throw new ApiError(400, "Vous ne pouvez pas vous faire un virement.");
                RateLimit(c, "transfer", Config.BankTransferCooldownSeconds);

                string error = game.TransferBank(me, target.Id, amount, $"Virement à {target.FullName} : {label}", $"Virement de {c.Player.FullName} : {label}", out bool delivered);
                if (error != null) throw new ApiError(400, error);
                if (!delivered)
                {
                    Db.With(db => db.Insert(new PendingCredit
                    {
                        CharacterId = target.Id, Amount = amount, Reason = $"Virement de {c.Player.FullName} : {label}", CreatedAt = Clock.Now
                    }));
                }
                Push(target.Id, "banque", "Virement reçu", $"{c.Player.FullName} vous a envoyé {amount:N2} € ({label}).", "success");
                return Ok(new { ok = true, to = target.FullName, amount, delivered });
            }
            return null;
        }

        private CharacterRef FindCharacter(string query)
        {
            string digits = Regex.Replace(query, "[^0-9]", "");
            if (PhonePattern.IsMatch(query) && digits.Length >= 3) return game.FindCharacterByPhone(digits);
            string[] parts = query.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 ? game.FindCharacterByName(parts[0], parts[1]) : null;
        }

        // ------------------------------------------------------------------
        //  SMS et contacts (tables du téléphone du jeu)
        // ------------------------------------------------------------------

        private HttpResponse Sms(ApiContext c, string[] p)
        {
            string myNumber = c.Player.PhoneNumber;
            if (string.IsNullOrEmpty(myNumber)) throw new ApiError(400, "Votre personnage n'a pas de numéro de téléphone.");

            if (c.Method == "POST" && p.Length == 1)
            {
                string to = Regex.Replace(c.Str("to", 30) ?? "", "[^0-9]", "");
                string message = c.Str("message", 500);
                if (to.Length < 3) throw new ApiError(400, "Numéro invalide.");
                if (message == null) throw new ApiError(400, "Message vide.");
                if (to == myNumber) throw new ApiError(400, "Vous ne pouvez pas vous écrire.");
                RateLimit(c, "sms", 2);
                string error = game.SendSms(c.CharacterId, myNumber, to, message);
                if (error != null) throw new ApiError(404, error);
                return Ok();
            }
            if (c.Method != "GET") return null;

            List<SmsItem> all = game.FetchSms(c.CharacterId);
            Dictionary<string, string> names = game.FetchContacts(c.CharacterId)
                .GroupBy(x => x.Number).ToDictionary(g => g.Key, g => g.First().Name);
            Func<SmsItem, string> other = s => s.From == myNumber ? s.To : s.From;

            if (p.Length == 1)
            {
                return HttpResponse.Json(all.GroupBy(other).Select(g =>
                {
                    SmsItem last = g.OrderByDescending(s => s.Timestamp).ThenByDescending(s => s.Id).First();
                    return new
                    {
                        number = g.Key,
                        name = names.TryGetValue(g.Key, out string n) ? n : null,
                        last = last.Message,
                        lastFromMe = last.From == myNumber,
                        timestamp = last.Timestamp,
                        count = g.Count()
                    };
                }).OrderByDescending(x => x.timestamp).ToList());
            }

            string number = Regex.Replace(p[1], "[^0-9]", "");
            return HttpResponse.Json(new
            {
                number,
                name = names.TryGetValue(number, out string name) ? name : null,
                myNumber,
                messages = all.Where(s => other(s) == number).OrderBy(s => s.Timestamp).ThenBy(s => s.Id)
                    .Select(s => new { id = s.Id, fromMe = s.From == myNumber, message = s.Message, timestamp = s.Timestamp })
            });
        }

        private HttpResponse Contacts(ApiContext c, string[] p)
        {
            if (p.Length == 1 && c.Method == "GET")
                return HttpResponse.Json(game.FetchContacts(c.CharacterId).OrderBy(x => x.Name)
                    .Select(x => new { id = x.Id, name = x.Name, number = x.Number }));
            if (p.Length == 1 && c.Method == "POST")
            {
                string name = c.Str("name", 40);
                string number = Regex.Replace(c.Str("number", 30) ?? "", "[^0-9]", "");
                if (name == null || number.Length < 3) throw new ApiError(400, "Nom et numéro obligatoires.");
                if (!game.CreateContact(c.CharacterId, name, number)) throw new ApiError(500, "Contact non enregistré.");
                return Ok();
            }
            if (p.Length == 2 && c.Method == "DELETE" && int.TryParse(p[1], out int id))
                return Ok(new { ok = game.DeleteContact(c.CharacterId, id) });
            return null;
        }

        // ------------------------------------------------------------------
        //  Mail (boîtes mail du jeu)
        // ------------------------------------------------------------------

        private MailAccountItem MyAccount(ApiContext c, string id)
        {
            int accountId = int.TryParse(id, out int v) ? v : -1;
            MailAccountItem account = game.GetMailAccounts(c.CharacterId).FirstOrDefault(a => a.Id == accountId);
            if (account == null) throw new ApiError(403, "Vous n'avez pas accès à cette boîte mail.");
            return account;
        }

        private HttpResponse Mail(ApiContext c, string[] p)
        {
            if (p.Length == 2 && p[1] == "accounts" && c.Method == "GET")
            {
                return HttpResponse.Json(new
                {
                    mine = game.GetMailAccounts(c.CharacterId).Select(a => new { id = a.Id, domain = a.Domain, name = a.Name }),
                    directory = game.GetAllMailAccounts().OrderBy(a => a.Name).Select(a => new { id = a.Id, domain = a.Domain, name = a.Name })
                });
            }
            if (p.Length == 2 && p[1] == "send" && c.Method == "POST")
            {
                MailAccountItem from = MyAccount(c, c.Str("from", 12));
                string toDomain = c.Str("to", 80);
                MailAccountItem to = game.GetAllMailAccounts().FirstOrDefault(a => string.Equals(a.Domain, toDomain, StringComparison.OrdinalIgnoreCase));
                if (to == null) throw new ApiError(404, "Adresse inconnue.");
                string subject = c.Str("subject", 120) ?? "(sans objet)";
                string content = c.Str("content", 4000);
                if (content == null) throw new ApiError(400, "Message vide.");
                RateLimit(c, "mail", 5);
                if (!game.SendMail(from, to, subject, content)) throw new ApiError(500, "Mail non envoyé.");
                foreach (int id in game.OnlineCharactersWithMailAccess(to.Id).Where(id => id != c.CharacterId))
                    Push(id, "mail", $"Mail · {to.Name}", $"{from.Name} : {subject}");
                return Ok();
            }
            if (p.Length == 2 && c.Method == "GET")
            {
                MailAccountItem account = MyAccount(c, p[1]);
                return HttpResponse.Json(game.FetchMails(account.Id).OrderByDescending(m => m.Id).Select(m => new
                {
                    id = m.Id, guid = m.Guid, sender = m.Sender, senderName = m.SenderName, recipient = m.Recipient,
                    recipientName = m.RecipientName, subject = m.Subject, content = m.Content, timestamp = m.Timestamp,
                    read = m.Read, isReply = m.IsReply
                }));
            }
            if (p.Length == 3 && c.Method == "DELETE")
            {
                MailAccountItem account = MyAccount(c, p[1]);
                game.DeleteMail(account.Id, c.CharacterId, p[2]);
                return Ok();
            }
            return null;
        }

        // ------------------------------------------------------------------
        //  Actualités
        // ------------------------------------------------------------------

        private string NewsOutlet(ApiContext c)
        {
            PlayerSnapshot s = c.Player;
            if (s.BizId > 0 && (Config.NewsPublisherBizIds?.Contains(s.BizId) == true
                || (Config.NewsPublisherActivities?.Length > 0 && game.IsProvider(c.CharacterId, Config.NewsPublisherActivities, new int[0]))))
                return s.BizName ?? "Presse";
            return s.IsAdmin ? "Administration" : null;
        }

        private HttpResponse News(ApiContext c, string[] p)
        {
            if (p.Length == 1 && c.Method == "GET")
            {
                List<NewsArticle> list = Db.With(db => db.Table<NewsArticle>().OrderByDescending(n => n.Id).Take(50).ToList());
                bool admin = c.Player.IsAdmin;
                return HttpResponse.Json(new
                {
                    canPublish = NewsOutlet(c) != null,
                    items = list.Select(n => new
                    {
                        id = n.Id, outlet = n.Outlet, author = n.AuthorName, title = n.Title, body = n.Body, createdAt = n.CreatedAt,
                        canDelete = admin || n.AuthorId == c.CharacterId
                    })
                });
            }
            if (p.Length == 1 && c.Method == "POST")
            {
                string outlet = NewsOutlet(c) ?? throw new ApiError(403, "Seuls la presse, les services publics autorisés et le staff peuvent publier.");
                string title = c.Str("title", 120);
                string body = c.Str("body", 5000);
                if (title == null || body == null) throw new ApiError(400, "Titre et texte obligatoires.");
                RateLimit(c, "news", 30);
                Db.With(db => db.Insert(new NewsArticle
                {
                    AuthorId = c.CharacterId, AuthorName = c.Player.FullName, Outlet = outlet, Title = title, Body = body, CreatedAt = Clock.Now
                }));
                if (Config.NewsNotifyPlayers)
                    foreach (int id in plugin.Players.OnlineCharacterIds().Where(id => id != c.CharacterId))
                        Push(id, "actualites", outlet, title, "info", false);
                return Ok();
            }
            if (p.Length == 2 && c.Method == "DELETE" && int.TryParse(p[1], out int nid))
            {
                NewsArticle a = Db.With(db => db.Find<NewsArticle>(nid)) ?? throw new ApiError(404, "Article introuvable.");
                if (a.AuthorId != c.CharacterId && !c.Player.IsAdmin) throw new ApiError(403, "Vous ne pouvez pas supprimer cet article.");
                Db.With(db => db.Delete<NewsArticle>(nid));
                return Ok();
            }
            return null;
        }

        // ------------------------------------------------------------------
        //  Birdy (réseau social)
        // ------------------------------------------------------------------

        private static string Handle(PlayerSnapshot s) => "@" + Regex.Replace($"{s.FirstName}_{s.LastName}", "[^A-Za-z0-9_À-ÿ]", "");

        private HttpResponse Birdy(ApiContext c, string[] p)
        {
            int me = c.CharacterId;
            if (p.Length == 1 && c.Method == "GET")
            {
                int before = int.TryParse(c.Req.Query.TryGetValue("before", out string b) ? b : null, out int bv) ? bv : int.MaxValue;
                List<BirdyPost> posts = Db.With(db => db.Table<BirdyPost>().Where(x => x.Id < before).OrderByDescending(x => x.Id).Take(30).ToList());
                List<int> ids = posts.Select(x => x.Id).ToList();
                HashSet<int> liked = new HashSet<int>(Db.With(db => db.Table<BirdyLike>().Where(l => l.CharacterId == me).ToList())
                    .Where(l => ids.Contains(l.PostId)).Select(l => l.PostId));
                return HttpResponse.Json(new
                {
                    me = new { name = c.Player.FullName, handle = Handle(c.Player) },
                    items = posts.Select(x => new
                    {
                        id = x.Id, author = x.AuthorName, handle = x.Handle, text = x.Text, likes = x.Likes, liked = liked.Contains(x.Id),
                        createdAt = x.CreatedAt, mine = x.AuthorId == me, canDelete = x.AuthorId == me || c.Player.IsAdmin
                    })
                });
            }
            if (p.Length == 1 && c.Method == "POST")
            {
                string text = c.Str("text", 280) ?? throw new ApiError(400, "Message vide.");
                RateLimit(c, "birdy", 10);
                Db.With(db => db.Insert(new BirdyPost
                {
                    AuthorId = me, AuthorName = c.Player.FullName, Handle = Handle(c.Player), Text = text, CreatedAt = Clock.Now
                }));
                return Ok();
            }
            if (p.Length >= 2 && int.TryParse(p[1], out int id))
            {
                BirdyPost post = Db.With(db => db.Find<BirdyPost>(id)) ?? throw new ApiError(404, "Message introuvable.");
                if (p.Length == 3 && p[2] == "like" && c.Method == "POST")
                {
                    bool liked = Db.With(db =>
                    {
                        BirdyLike existing = db.Table<BirdyLike>().Where(l => l.PostId == id && l.CharacterId == me).FirstOrDefault();
                        if (existing != null) db.Delete<BirdyLike>(existing.Id);
                        else db.Insert(new BirdyLike { PostId = id, CharacterId = me });
                        post.Likes = db.Table<BirdyLike>().Where(l => l.PostId == id).Count();
                        db.Update(post);
                        return existing == null;
                    });
                    if (liked && post.AuthorId != me) Push(post.AuthorId, "birdy", "Birdy", $"{c.Player.FullName} aime votre message.", "info", false);
                    return Ok(new { liked, likes = post.Likes });
                }
                if (p.Length == 2 && c.Method == "DELETE")
                {
                    if (post.AuthorId != me && !c.Player.IsAdmin) throw new ApiError(403, "Vous ne pouvez pas supprimer ce message.");
                    Db.With(db =>
                    {
                        db.Delete<BirdyPost>(id);
                        return db.Execute("DELETE FROM birdy_likes WHERE PostId = ?", id);
                    });
                    return Ok();
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        //  Véhicules et immobilier (GPS en jeu)
        // ------------------------------------------------------------------

        private HttpResponse Vehicles(ApiContext c, string[] p)
        {
            if (p.Length == 1 && c.Method == "GET")
                return HttpResponse.Json(game.GetVehicles(c.CharacterId).Select(v => new
                {
                    id = v.Id, plate = v.Plate, model = v.Model, fuel = Math.Round(v.Fuel), stowed = v.Stowed, owner = v.Owner, bizId = v.BizId, color = v.Color
                }));
            if (p.Length == 3 && p[2] == "locate" && c.Method == "POST" && int.TryParse(p[1], out int id))
            {
                Position pos = game.GetVehiclePosition(c.CharacterId, id) ?? throw new ApiError(404, "Véhicule introuvable ou rangé au garage.");
                if (!game.SetGps(c.CharacterId, pos)) throw new ApiError(400, "Impossible de placer le point GPS.");
                return Ok();
            }
            return null;
        }

        private HttpResponse RealEstate(ApiContext c, string[] p)
        {
            if (p.Length == 1 && c.Method == "GET")
            {
                List<AreaItem> areas = game.GetAreas(c.CharacterId);
                Func<AreaItem, object> map = a => new { id = a.Id, address = a.Address, price = a.Price, rentPrice = a.RentPrice, rentable = a.Rentable, owner = a.Owner, bizId = a.BizId };
                return HttpResponse.Json(new
                {
                    mine = areas.Where(a => a.Mine).Select(map),
                    forSale = areas.Where(a => a.ForSale).OrderBy(a => a.Price).Select(map)
                });
            }
            if (p.Length == 3 && p[2] == "locate" && c.Method == "POST" && uint.TryParse(p[1], out uint id))
            {
                Position pos = game.GetAreaPosition(id) ?? throw new ApiError(404, "Terrain introuvable.");
                if (!game.SetGps(c.CharacterId, pos)) throw new ApiError(400, "Impossible de placer le point GPS.");
                return Ok();
            }
            return null;
        }

        // ------------------------------------------------------------------
        //  Le Bon Plan (petites annonces)
        // ------------------------------------------------------------------

        private static readonly string[] AdCategories = { "Véhicules", "Immobilier", "Objets", "Emploi", "Services", "Autre" };

        private HttpResponse Ads(ApiContext c, string[] p)
        {
            if (p.Length == 1 && c.Method == "GET")
            {
                long now = Clock.Now;
                Db.With(db => db.Execute("DELETE FROM ads WHERE ExpiresAt < ?", now));
                List<Ad> ads = Db.With(db => db.Table<Ad>().OrderByDescending(a => a.Id).Take(200).ToList());
                return HttpResponse.Json(new
                {
                    categories = AdCategories,
                    items = ads.Select(a => new
                    {
                        id = a.Id, author = a.AuthorName, phone = a.Phone, category = a.Category, title = a.Title, description = a.Description,
                        price = a.Price, createdAt = a.CreatedAt, expiresAt = a.ExpiresAt,
                        mine = a.AuthorId == c.CharacterId, canDelete = a.AuthorId == c.CharacterId || c.Player.IsAdmin
                    })
                });
            }
            if (p.Length == 1 && c.Method == "POST")
            {
                string title = c.Str("title", 80) ?? throw new ApiError(400, "Titre obligatoire.");
                string category = c.Str("category", 30);
                if (!AdCategories.Contains(category)) category = "Autre";
                double price = Math.Max(0, Math.Round(c.Body["price"]?.Value<double?>() ?? 0, 2));
                int mine = Db.With(db => db.Table<Ad>().Where(a => a.AuthorId == c.CharacterId).Count());
                if (mine >= Config.AdsMaxPerPlayer) throw new ApiError(400, $"Maximum {Config.AdsMaxPerPlayer} annonces actives.");
                RateLimit(c, "ads", 20);
                Db.With(db => db.Insert(new Ad
                {
                    AuthorId = c.CharacterId, AuthorName = c.Player.FullName, Phone = c.Player.PhoneNumber, Category = category, Title = title,
                    Description = c.Str("description", 1000) ?? "", Price = price, CreatedAt = Clock.Now, ExpiresAt = Clock.Now + Config.AdsLifetimeDays * 86400L
                }));
                return Ok();
            }
            if (p.Length == 2 && c.Method == "DELETE" && int.TryParse(p[1], out int id))
            {
                Ad ad = Db.With(db => db.Find<Ad>(id)) ?? throw new ApiError(404, "Annonce introuvable.");
                if (ad.AuthorId != c.CharacterId && !c.Player.IsAdmin) throw new ApiError(403, "Ce n'est pas votre annonce.");
                Db.With(db => db.Delete<Ad>(id));
                return Ok();
            }
            return null;
        }

        // ------------------------------------------------------------------
        //  Services : taxi, bus, livraison, santé, assurances... (envoi aux joueurs en service)
        // ------------------------------------------------------------------

        private ServiceConfig ServiceOf(string id) =>
            Config.Services?.FirstOrDefault(s => s.Id == id) ?? throw new ApiError(404, "Service inconnu.");

        private object RequestDto(ServiceRequest r, int me) => new
        {
            id = r.Id, service = r.ServiceId, requester = r.RequesterName, requesterPhone = r.RequesterPhone, message = r.Message,
            hasPosition = r.HasPosition, status = r.Status, acceptedBy = r.AcceptedByName, acceptedByPhone = r.AcceptedByPhone,
            createdAt = r.CreatedAt, updatedAt = r.UpdatedAt, mine = r.RequesterId == me, acceptedByMe = r.AcceptedById == me
        };

        private void ExpireRequests()
        {
            long limit = Clock.Now - Config.ServiceRequestLifetimeMinutes * 60L;
            Db.With(db => db.Execute("UPDATE service_requests SET Status = 'expired' WHERE Status IN ('open','accepted') AND UpdatedAt < ?", limit));
        }

        private HttpResponse Services(ApiContext c, string[] p)
        {
            int me = c.CharacterId;
            if (p.Length == 1 && c.Method == "GET")
            {
                return HttpResponse.Json((Config.Services ?? new List<ServiceConfig>()).Select(s => new
                {
                    id = s.Id, name = s.Name, requestLabel = s.RequestLabel, placeholder = s.Placeholder,
                    configured = s.Activities?.Length > 0 || s.BizIds?.Length > 0
                }));
            }

            if (p.Length == 4 && p[1] == "requests" && int.TryParse(p[2], out int rid) && c.Method == "POST")
                return RequestAction(c, rid, p[3]);

            if (p.Length >= 2)
            {
                ServiceConfig s = ServiceOf(p[1]);
                ExpireRequests();

                if (p.Length == 2 && c.Method == "GET")
                {
                    bool provider = game.IsProvider(me, s.Activities, s.BizIds);
                    string sid = s.Id;
                    List<ServiceRequest> requests = Db.With(db => db.Table<ServiceRequest>().Where(r => r.ServiceId == sid).OrderByDescending(r => r.Id).Take(100).ToList());
                    HashSet<int> bizIds = new HashSet<int>(s.BizIds ?? new int[0]);
                    HashSet<string> acts = new HashSet<string>(s.Activities ?? new string[0], StringComparer.OrdinalIgnoreCase);
                    return HttpResponse.Json(new
                    {
                        service = new { id = s.Id, name = s.Name, requestLabel = s.RequestLabel, placeholder = s.Placeholder, configured = acts.Count > 0 || bizIds.Count > 0 },
                        provider,
                        onDuty = c.Player.OnDuty,
                        available = game.OnlineProviders(s.Activities, s.BizIds, s.RequireOnDuty).Count,
                        businesses = game.GetBizs().Where(b => bizIds.Contains(b.Id) || b.Activities.Any(acts.Contains))
                            .Select(b => new { id = b.Id, name = b.Name, contact = b.Contact, description = b.Description }),
                        mine = requests.Where(r => r.RequesterId == me).Take(10).Select(r => RequestDto(r, me)),
                        queue = provider
                            ? requests.Where(r => (r.Status == "open" && r.RequesterId != me) || (r.Status == "accepted" && r.AcceptedById == me)).Select(r => RequestDto(r, me))
                            : Enumerable.Empty<object>()
                    });
                }

                if (p.Length == 3 && p[2] == "requests" && c.Method == "POST")
                {
                    if (!(s.Activities?.Length > 0 || s.BizIds?.Length > 0)) throw new ApiError(400, "Ce service n'est pas encore configuré sur le serveur.");
                    string sid = s.Id;
                    bool hasOpen = Db.With(db => db.Table<ServiceRequest>().Where(r => r.ServiceId == sid && r.RequesterId == me && (r.Status == "open" || r.Status == "accepted")).Count()) > 0;
                    if (hasOpen) throw new ApiError(400, "Vous avez déjà une demande en cours pour ce service.");
                    RateLimit(c, "service:" + s.Id, 15);

                    List<int> providers = game.OnlineProviders(s.Activities, s.BizIds, s.RequireOnDuty);
                    if (providers.Count == 0) throw new ApiError(404, "Personne n'est disponible pour le moment.");

                    bool share = c.Body["sharePosition"]?.Value<bool?>() ?? true;
                    Position? pos = share ? game.GetPlayerPosition(me) : null;
                    ServiceRequest req = new ServiceRequest
                    {
                        ServiceId = s.Id, RequesterId = me, RequesterName = c.Player.FullName, RequesterPhone = c.Player.PhoneNumber,
                        Message = c.Str("message", 300) ?? "", HasPosition = pos.HasValue, X = pos?.X ?? 0, Y = pos?.Y ?? 0, Z = pos?.Z ?? 0,
                        Status = "open", CreatedAt = Clock.Now, UpdatedAt = Clock.Now
                    };
                    Db.With(db => db.Insert(req));
                    foreach (int id in providers.Where(id => id != me))
                        Push(id, s.Id, $"{s.Name} : nouvelle demande", $"{c.Player.FullName} : {(req.Message.Length > 0 ? req.Message : "demande sans message")}", "warning");
                    return Ok(new { ok = true, notified = providers.Count });
                }
            }
            return null;
        }

        private HttpResponse RequestAction(ApiContext c, int rid, string action)
        {
            int me = c.CharacterId;
            ServiceRequest r = Db.With(db => db.Find<ServiceRequest>(rid)) ?? throw new ApiError(404, "Demande introuvable.");
            ServiceConfig s = ServiceOf(r.ServiceId);
            bool provider = game.IsProvider(me, s.Activities, s.BizIds);

            switch (action)
            {
                case "accept":
                    if (!provider) throw new ApiError(403, "Vous n'êtes pas membre de ce service.");
                    if (r.Status != "open") throw new ApiError(400, "Cette demande n'est plus disponible.");
                    r.Status = "accepted";
                    r.AcceptedById = me;
                    r.AcceptedByName = c.Player.FullName;
                    r.AcceptedByPhone = c.Player.PhoneNumber;
                    Save(r);
                    Push(r.RequesterId, s.Id, s.Name, $"{c.Player.FullName} a accepté votre demande et arrive.", "success");
                    if (r.HasPosition) game.SetGps(me, new Position(r.X, r.Y, r.Z));
                    return Ok(new { ok = true, gps = r.HasPosition });

                case "locate":
                    if (r.AcceptedById != me || !r.HasPosition) throw new ApiError(400, "Pas de position pour cette demande.");
                    game.SetGps(me, new Position(r.X, r.Y, r.Z));
                    return Ok();

                case "done":
                    if (r.AcceptedById != me && r.RequesterId != me) throw new ApiError(403, "Action impossible.");
                    if (r.Status != "accepted" && r.Status != "open") throw new ApiError(400, "Demande déjà terminée.");
                    r.Status = "done";
                    Save(r);
                    if (r.RequesterId != me) Push(r.RequesterId, s.Id, s.Name, "Votre demande est terminée.", "success", false);
                    return Ok();

                case "cancel":
                    if (r.RequesterId != me && r.AcceptedById != me) throw new ApiError(403, "Action impossible.");
                    if (r.Status != "accepted" && r.Status != "open") throw new ApiError(400, "Demande déjà terminée.");
                    if (r.RequesterId == me)
                    {
                        r.Status = "cancelled";
                        if (r.AcceptedById > 0) Push(r.AcceptedById, s.Id, s.Name, $"{r.RequesterName} a annulé sa demande.", "warning");
                    }
                    else
                    {
                        // Le prestataire se désiste : la demande redevient disponible
                        r.Status = "open";
                        r.AcceptedById = 0;
                        r.AcceptedByName = null;
                        r.AcceptedByPhone = null;
                        Push(r.RequesterId, s.Id, s.Name, "Le prestataire s'est désisté, votre demande est de nouveau en attente.", "warning");
                    }
                    Save(r);
                    return Ok();
            }
            throw new ApiError(404, "Action inconnue.");
        }

        private void Save(ServiceRequest r)
        {
            r.UpdatedAt = Clock.Now;
            Db.With(db => db.Update(r));
        }

        private static string Trim(string s, int max) => s == null ? null : (s.Length > max ? s.Substring(0, max) : s);
    }
}
