using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using Newtonsoft.Json;
using UnityEngine;

namespace EasyLifeTablette
{
    /// <summary>
    /// Tablette Easy Life, 100 % en jeu (menus Nova-Life) : /tablette ou la touche configurée.
    /// Applications : Mail, Annuaire, Actualités, Le Bon Plan, Commander, Banque, Notes,
    /// Notifications, Réglages et Administration. Les données sont dans Plugins/EasyLifeTablette/data.json.
    /// </summary>
    public class EasyLifeTablettePlugin : Plugin
    {
        internal static EasyLifeTablettePlugin Instance;

        internal Config Config;
        internal Store Store;

        /// <summary>Joueurs qui ont saisi leur code depuis leur connexion.</summary>
        private readonly HashSet<string> unlocked = new HashSet<string>();
        /// <summary>Panier en cours par joueur (Commander).</summary>
        private readonly Dictionary<string, Cart> carts = new Dictionary<string, Cart>();
        /// <summary>Équipiers en service par entreprise (Commander).</summary>
        private readonly Dictionary<int, HashSet<string>> onDuty = new Dictionary<int, HashSet<string>>();
        private readonly Dictionary<string, float> lastKey = new Dictionary<string, float>();

        public EasyLifeTablettePlugin(IGameAPI api) : base(api) { }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            Instance = this;

            string folder = Path.Combine(pluginsPath, "EasyLifeTablette");
            Config = Config.Load(Path.Combine(folder, "config.json"));
            Store = Store.Load(Path.Combine(folder, "data.json"));

            GameObject runner = new GameObject("EasyLifeTablette");
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.AddComponent<Runner>();

            new SChatCommand("/tablette", new string[] { "/tab" }, "Ouvrir la tablette " + Config.ServerName, "/tablette",
                (Action<Player, string[]>)((player, args) => Open(player))).Register();

            Debug.Log("[EasyLifeTablette] tablette en jeu chargée (/tablette)");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            Profile profile = Store.ProfileOf(player);
            // Le code est redemandé à chaque connexion
            unlocked.Remove(profile.SteamId);

            // Remboursements ou paiements reçus pendant que le joueur était hors ligne
            if (profile.PendingBank != 0)
            {
                player.AddBankMoney(profile.PendingBank, "Tablette " + Config.ServerName);
                Notify(profile, "banque", "Banque", "Opérations reçues en votre absence : " + Fmt.Money(profile.PendingBank));
                profile.PendingBank = 0;
                Store.Save();
            }

            int unread = profile.Notifications.Count(n => !n.Read);
            if (unread > 0)
                player.Notify("Tablette", unread + " nouvelle(s) notification(s). Tapez /tablette.", NotificationManager.Type.Info, 8f);
        }

        public override void OnPlayerDisconnect(NetworkConnection conn)
        {
            base.OnPlayerDisconnect(conn);
            HashSet<string> online = new HashSet<string>(OnlinePlayers().Select(p => p.steamId.ToString()));
            unlocked.RemoveWhere(id => !online.Contains(id));
            foreach (HashSet<string> team in onDuty.Values) team.RemoveWhere(id => !online.Contains(id));
        }

        public override void OnPlayerInput(Player player, KeyCode keyCode, bool state)
        {
            base.OnPlayerInput(player, keyCode, state);
            if (Config.Key == KeyCode.None || keyCode != Config.Key) return;
            // La touche peut remonter plusieurs évènements d'affilée
            string id = player.steamId.ToString();
            if (lastKey.TryGetValue(id, out float last) && Time.unscaledTime - last < 1f) return;
            lastKey[id] = Time.unscaledTime;
            Open(player);
        }

        // ==================================================================
        //  Outils
        // ==================================================================

        internal static List<Player> OnlinePlayers()
        {
            return (Nova.server.GetAllInGamePlayers() ?? new List<Player>()).Where(p => p != null && p.character != null).ToList();
        }

        internal static Player FindOnline(string steamId)
        {
            return OnlinePlayers().FirstOrDefault(p => p.steamId.ToString() == steamId);
        }

        /// <summary>Notification tablette + à l'écran si le joueur est en ville.</summary>
        internal void Notify(Profile profile, string app, string title, string body)
        {
            profile.Notifications.Insert(0, new Notif { App = app, Title = title, Body = body, Date = DateTime.Now });
            if (profile.Notifications.Count > 50) profile.Notifications.RemoveRange(50, profile.Notifications.Count - 50);
            Store.Save();
            Player target = FindOnline(profile.SteamId);
            if (target != null && profile.GameNotifications)
                target.Notify(title, body, NotificationManager.Type.Info, 8f);
        }

        internal void Notify(string steamId, string app, string title, string body)
        {
            Profile profile;
            if (Store.Data.Profiles.TryGetValue(steamId, out profile)) Notify(profile, app, title, body);
        }

        /// <summary>Ajoute (ou retire) de l'argent en banque, même si le joueur est hors ligne.</summary>
        internal void PayPlayer(string steamId, double amount, string reason)
        {
            Player target = FindOnline(steamId);
            if (target != null) target.AddBankMoney(amount, reason);
            else if (Store.Data.Profiles.ContainsKey(steamId)) Store.Data.Profiles[steamId].PendingBank += amount;
            Store.Save();
        }

        // ==================================================================
        //  Briques de menus
        // ==================================================================

        /// <summary>Ferme le menu courant puis ouvre le suivant.</summary>
        internal static Action<UIPanel> Go(Player player, Action next)
        {
            return p => { player.ClosePanel(p); next(); };
        }

        internal static UIPanel Tab(string title)
        {
            return new UIPanel(title, UIPanel.PanelType.Tab);
        }

        /// <summary>Boutons standards d'une liste : Ouvrir (si non vide), Retour, Fermer.</summary>
        internal static void ListButtons(Player player, UIPanel panel, bool hasLines, Action back, string openLabel = "Ouvrir")
        {
            if (!hasLines) panel.AddTabLine(Fmt.Grey("(vide)"), p => { });
            else panel.AddButton(openLabel, p => p.SelectTab());
            if (back != null) panel.AddButton("Retour", Go(player, back));
            panel.AddButton("Fermer", p => player.ClosePanel(p));
            player.ShowPanelUI(panel);
        }

        internal static void Message(Player player, string title, string text, Action back)
        {
            UIPanel panel = new UIPanel(title, UIPanel.PanelType.Text).SetText(text);
            panel.AddButton("Retour", Go(player, back));
            panel.AddButton("Fermer", p => player.ClosePanel(p));
            player.ShowPanelUI(panel);
        }

        /// <summary>Champ de saisie : onOk reçoit le texte (nettoyé), onBack ramène au menu précédent.</summary>
        internal static void Ask(Player player, string title, string text, string placeholder, Action<string> onOk, Action onBack, bool password = false)
        {
            UIPanel panel = new UIPanel(title, UIPanel.PanelType.Input).SetText(text).SetInputPlaceholder(placeholder ?? "");
            panel.isPassword = password;
            panel.AddButton("Valider", p =>
            {
                string value = Fmt.Clean(p.inputText);
                player.ClosePanel(p);
                onOk(value);
            });
            panel.AddButton("Retour", Go(player, onBack));
            player.ShowPanelUI(panel);
        }

        internal static void Error(Player player, string message)
        {
            player.Notify("Tablette", message, NotificationManager.Type.Error, 5f);
        }

        internal static void Success(Player player, string message)
        {
            player.Notify("Tablette", message, NotificationManager.Type.Success, 5f);
        }

        // ==================================================================
        //  Accueil et verrouillage
        // ==================================================================

        internal void Open(Player player)
        {
            Profile me = Store.ProfileOf(player);
            if (!string.IsNullOrEmpty(me.Pin) && !unlocked.Contains(me.SteamId))
            {
                Ask(player, "Tablette verrouillée", "Saisissez votre code pour déverrouiller la tablette.", "Code", code =>
                {
                    if (code == me.Pin)
                    {
                        unlocked.Add(me.SteamId);
                        Home(player);
                    }
                    else
                    {
                        Error(player, "Code incorrect.");
                    }
                }, () => { }, true);
                return;
            }
            Home(player);
        }

        internal void Home(Player player)
        {
            Profile me = Store.ProfileOf(player);
            int unreadMails = Store.Inbox(me.SteamId).Count(m => !m.ReadBy.Contains(me.SteamId));
            int unreadNotifs = me.Notifications.Count(n => !n.Read);
            Article last = Store.Data.News.OrderByDescending(a => a.Pinned).ThenByDescending(a => a.Date).FirstOrDefault();

            UIPanel panel = Tab(Config.ServerName + "  •  " + DateTime.Now.ToString("dddd d MMMM  HH:mm", Fmt.Fr));
            panel.AddTabLine(Fmt.App("Mail") + Fmt.Badge(unreadMails, "non lu"), Go(player, () => Mail.Home(player)));
            panel.AddTabLine(Fmt.App("Annuaire"), Go(player, () => Directory.Home(player)));
            panel.AddTabLine(Fmt.App("Actualités") + (last != null ? Fmt.Grey("  " + Fmt.Cut(last.Title, 40)) : ""), Go(player, () => News.Home(player)));
            panel.AddTabLine(Fmt.App("Le Bon Plan") + Fmt.Grey("  " + Store.Data.Ads.Count(a => a.IsActive) + " annonce(s)"), Go(player, () => Ads.Home(player)));
            panel.AddTabLine(Fmt.App("Commander") + Fmt.Grey("  restaurants de la ville"), Go(player, () => Food.Home(player)));
            panel.AddTabLine(Fmt.App("Banque") + Fmt.Grey("  " + Fmt.Money(player.Bank)), Go(player, () => Bank.Home(player)));
            panel.AddTabLine(Fmt.App("Notes") + Fmt.Grey("  " + me.Notes.Count + " note(s)"), Go(player, () => Notes.Home(player)));
            panel.AddTabLine(Fmt.App("Notifications") + Fmt.Badge(unreadNotifs, "nouvelle"), Go(player, () => ShowNotifications(player)));
            panel.AddTabLine(Fmt.App("Réglages"), Go(player, () => Settings.Home(player)));
            if (player.IsAdmin) panel.AddTabLine(Fmt.App("Administration"), Go(player, () => Admin.Home(player)));
            ListButtons(player, panel, true, null);
        }

        private void ShowNotifications(Player player)
        {
            Profile me = Store.ProfileOf(player);
            StringBuilder text = new StringBuilder();
            if (me.Notifications.Count == 0) text.Append("Aucune notification.");
            foreach (Notif n in me.Notifications.Take(15))
            {
                text.Append(n.Read ? "" : Fmt.Color("● ", "#ff9f0a"))
                    .Append("<b>").Append(n.Title).Append("</b>  ").Append(Fmt.Grey(Fmt.Ago(n.Date))).Append('\n')
                    .Append(n.Body).Append("\n\n");
                n.Read = true;
            }
            Store.Save();

            UIPanel panel = new UIPanel("Notifications", UIPanel.PanelType.Text).SetText(text.ToString());
            if (me.Notifications.Count > 0)
                panel.AddButton("Tout effacer", Go(player, () => { me.Notifications.Clear(); Store.Save(); Home(player); }));
            panel.AddButton("Retour", Go(player, () => Home(player)));
            panel.AddButton("Fermer", p => player.ClosePanel(p));
            player.ShowPanelUI(panel);
        }

        // ==================================================================
        //  Commander : état en mémoire (paniers, service)
        // ==================================================================

        internal Cart CartOf(Player player)
        {
            string id = player.steamId.ToString();
            Cart cart;
            if (!carts.TryGetValue(id, out cart)) carts[id] = cart = new Cart();
            return cart;
        }

        internal HashSet<string> DutyOf(int bizId)
        {
            HashSet<string> team;
            if (!onDuty.TryGetValue(bizId, out team)) onDuty[bizId] = team = new HashSet<string>();
            return team;
        }

        internal void Lock(Player player)
        {
            unlocked.Remove(player.steamId.ToString());
        }

        /// <summary>Composant Unity : enregistre les données sur le disque quelques secondes après une modification.</summary>
        private class Runner : MonoBehaviour
        {
            private float nextFlush;

            private void Update()
            {
                if (Instance == null || Time.unscaledTime < nextFlush) return;
                nextFlush = Time.unscaledTime + 3f;
                Instance.Store.Flush();
            }

            private void OnApplicationQuit()
            {
                if (Instance != null) Instance.Store.Flush();
            }
        }
    }

    // ======================================================================
    //  Mail
    // ======================================================================

    internal static class Mail
    {
        private static EasyLifeTablettePlugin T { get { return EasyLifeTablettePlugin.Instance; } }

        public static void Home(Player player)
        {
            Profile me = T.Store.ProfileOf(player);
            int unread = T.Store.Inbox(me.SteamId).Count(m => !m.ReadBy.Contains(me.SteamId));
            UIPanel panel = EasyLifeTablettePlugin.Tab("Mail  •  " + me.Mail);
            panel.AddTabLine("Boîte de réception" + Fmt.Badge(unread, "non lu"), EasyLifeTablettePlugin.Go(player, () => List(player, true)));
            panel.AddTabLine("Envoyés", EasyLifeTablettePlugin.Go(player, () => List(player, false)));
            panel.AddTabLine("Écrire un message", EasyLifeTablettePlugin.Go(player, () => PickRecipient(player, null, null, () => Home(player))));
            EasyLifeTablettePlugin.ListButtons(player, panel, true, () => T.Home(player));
        }

        private static void List(Player player, bool inbox)
        {
            Profile me = T.Store.ProfileOf(player);
            List<MailMessage> mails = inbox ? T.Store.Inbox(me.SteamId) : T.Store.Sent(me.SteamId);
            UIPanel panel = EasyLifeTablettePlugin.Tab(inbox ? "Boîte de réception" : "Envoyés");
            foreach (MailMessage m in mails.Take(60))
            {
                bool unread = inbox && !m.ReadBy.Contains(me.SteamId);
                string who = inbox ? m.FromName : "À : " + string.Join(", ", m.To.Select(T.Store.NameOf));
                panel.AddTabLine((unread ? Fmt.Color("● ", "#0a84ff") : "") + "<b>" + Fmt.Cut(who, 28) + "</b>  " + Fmt.Cut(m.Subject, 40) + "  " + Fmt.Grey(Fmt.Ago(m.Date)),
                    EasyLifeTablettePlugin.Go(player, () => Read(player, m, inbox)));
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, mails.Count > 0, () => Home(player), "Lire");
        }

        private static void Read(Player player, MailMessage m, bool inbox)
        {
            Profile me = T.Store.ProfileOf(player);
            if (!m.ReadBy.Contains(me.SteamId)) { m.ReadBy.Add(me.SteamId); T.Store.Save(); }

            string text = "<b>De :</b> " + m.FromName + " " + Fmt.Grey("<" + T.Store.MailOf(m.From) + ">") + "\n" +
                          "<b>À :</b> " + string.Join(", ", m.To.Select(T.Store.NameOf)) + "\n" +
                          "<b>Objet :</b> " + m.Subject + "\n" + Fmt.Grey(m.Date.ToString("dd/MM/yyyy HH:mm")) + "\n\n" + m.Body;
            UIPanel panel = new UIPanel("Mail", UIPanel.PanelType.Text).SetText(text);
            if (inbox)
                panel.AddButton("Répondre", EasyLifeTablettePlugin.Go(player, () =>
                    Compose(player, new List<string> { m.From }, m.Subject.StartsWith("RE :") ? m.Subject : "RE : " + m.Subject, () => Read(player, m, inbox))));
            panel.AddButton("Supprimer", EasyLifeTablettePlugin.Go(player, () =>
            {
                m.DeletedBy.Add(me.SteamId);
                T.Store.Save();
                List(player, inbox);
            }));
            panel.AddButton("Retour", EasyLifeTablettePlugin.Go(player, () => List(player, inbox)));
            player.ShowPanelUI(panel);
        }

        /// <summary>Choix du destinataire (joueurs en ville d'abord), avec recherche.</summary>
        public static void PickRecipient(Player player, string search, string subject, Action back)
        {
            Profile me = T.Store.ProfileOf(player);
            HashSet<string> online = new HashSet<string>(EasyLifeTablettePlugin.OnlinePlayers().Select(p => p.steamId.ToString()));
            IEnumerable<Profile> people = T.Store.Data.Profiles.Values.Where(p => p.SteamId != me.SteamId);
            if (!string.IsNullOrEmpty(search))
            {
                string q = search.ToLowerInvariant();
                people = people.Where(p => (p.Name + " " + p.Mail + " " + p.Phone).ToLowerInvariant().Contains(q));
            }
            List<Profile> list = people.OrderByDescending(p => online.Contains(p.SteamId)).ThenBy(p => p.Name).Take(80).ToList();

            UIPanel panel = EasyLifeTablettePlugin.Tab("Destinataire" + (string.IsNullOrEmpty(search) ? "" : "  •  « " + search + " »"));
            panel.AddTabLine(Fmt.Color("Rechercher un nom, une adresse, un numéro…", "#0a84ff"), EasyLifeTablettePlugin.Go(player, () =>
                EasyLifeTablettePlugin.Ask(player, "Rechercher", "Nom, adresse mail ou numéro du destinataire", "Jean Dupont",
                    q => PickRecipient(player, q, subject, back), () => PickRecipient(player, search, subject, back))));
            foreach (Profile p in list)
            {
                Profile target = p;
                panel.AddTabLine((online.Contains(p.SteamId) ? Fmt.Color("● ", "#30d158") : "") + p.Name + "  " + Fmt.Grey(p.Mail),
                    EasyLifeTablettePlugin.Go(player, () => Compose(player, new List<string> { target.SteamId }, subject, back)));
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, true, back, "Choisir");
        }

        /// <summary>Écrire un mail : objet (si absent) puis message.</summary>
        public static void Compose(Player player, List<string> to, string subject, Action back)
        {
            string names = string.Join(", ", to.Select(T.Store.NameOf));
            if (string.IsNullOrEmpty(subject))
            {
                EasyLifeTablettePlugin.Ask(player, "Nouveau mail", "À : " + names + "\nObjet du message :", "Objet",
                    s => Compose(player, to, string.IsNullOrEmpty(s) ? "(Sans objet)" : Fmt.Cut(s, 80), back), back);
                return;
            }
            EasyLifeTablettePlugin.Ask(player, "Nouveau mail", "À : " + names + "\nObjet : " + subject + "\n\nVotre message :", "Message", body =>
            {
                if (string.IsNullOrEmpty(body))
                {
                    EasyLifeTablettePlugin.Error(player, "Le message est vide.");
                    Compose(player, to, subject, back);
                    return;
                }
                Send(T.Store.ProfileOf(player), to, subject, body);
                EasyLifeTablettePlugin.Success(player, "Mail envoyé à " + names + ".");
                back();
            }, back);
        }

        public static void Send(Profile from, List<string> to, string subject, string body)
        {
            T.Store.Data.Mails.Add(new MailMessage
            {
                Id = T.Store.NextId(),
                From = from.SteamId,
                FromName = from.Name,
                To = to,
                Subject = subject,
                Body = Fmt.Cut(body, 1000),
                Date = DateTime.Now,
            });
            foreach (string id in to)
                T.Notify(id, "mail", "Mail de " + from.Name, subject);
            T.Store.Save();
        }
    }

    // ======================================================================
    //  Annuaire
    // ======================================================================

    internal static class Directory
    {
        private static EasyLifeTablettePlugin T { get { return EasyLifeTablettePlugin.Instance; } }

        public static void Home(Player player)
        {
            List<DirEntry> entries = All();
            UIPanel panel = EasyLifeTablettePlugin.Tab("Annuaire  •  " + entries.Count + " fiche(s)");
            foreach (DirEntry e in entries)
            {
                DirEntry entry = e;
                panel.AddTabLine("<b>" + e.Name + "</b>  " + Fmt.Grey(e.Category) +
                                 (string.IsNullOrEmpty(e.Phone) ? "" : "   " + e.Phone) +
                                 (e.Recruiting ? "   " + Fmt.Color("Recrute", "#30d158") : ""),
                    EasyLifeTablettePlugin.Go(player, () => Detail(player, entry)));
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, entries.Count > 0, () => T.Home(player));
        }

        /// <summary>Entreprises du serveur + fiches ajoutées par le staff, triées par nom.</summary>
        public static List<DirEntry> All()
        {
            List<DirEntry> list = new List<DirEntry>();
            if (Nova.biz != null && Nova.biz.bizs != null)
            {
                foreach (Bizs b in Nova.biz.bizs)
                {
                    if (b == null || string.IsNullOrWhiteSpace(b.BizName)) continue;
                    list.Add(new DirEntry
                    {
                        Id = -b.Id,
                        BizId = b.Id,
                        Name = b.BizName,
                        Category = "Entreprise",
                        Phone = b.Contact ?? "",
                        Description = b.Description ?? "",
                        Recruiting = b.IsRecruiting,
                    });
                }
            }
            list.AddRange(T.Store.Data.Directory);
            return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void Detail(Player player, DirEntry e)
        {
            int open = e.BizId > 0 ? T.DutyOf(e.BizId).Count : 0;
            string text = "<b>" + e.Name + "</b>\n" + Fmt.Grey(e.Category) + "\n\n" +
                          "<b>Téléphone :</b> " + (string.IsNullOrEmpty(e.Phone) ? "non renseigné" : e.Phone) + "\n" +
                          (e.Recruiting ? Fmt.Color("Recrute actuellement", "#30d158") + "\n" : "") +
                          (e.BizId > 0 && T.Store.Data.Menu.Any(i => i.BizId == e.BizId)
                              ? (open > 0 ? Fmt.Color("Restaurant ouvert", "#30d158") : Fmt.Color("Restaurant fermé", "#ff453a")) + " (Commander)\n" : "") +
                          (string.IsNullOrEmpty(e.Description) ? "" : "\n" + e.Description);

            UIPanel panel = new UIPanel(e.Name, UIPanel.PanelType.Text).SetText(text);
            if (e.BizId > 0)
            {
                Profile owner = T.Store.OwnerOf(e.BizId);
                if (owner != null && owner.SteamId != player.steamId.ToString())
                    panel.AddButton("Écrire au gérant", EasyLifeTablettePlugin.Go(player, () =>
                        Mail.Compose(player, new List<string> { owner.SteamId }, "Annuaire : " + e.Name, () => Detail(player, e))));
            }
            else if (player.IsAdmin)
            {
                panel.AddButton("Supprimer", EasyLifeTablettePlugin.Go(player, () =>
                {
                    T.Store.Data.Directory.RemoveAll(d => d.Id == e.Id);
                    T.Store.Save();
                    EasyLifeTablettePlugin.Success(player, "Fiche supprimée.");
                    Home(player);
                }));
            }
            panel.AddButton("Retour", EasyLifeTablettePlugin.Go(player, () => Home(player)));
            player.ShowPanelUI(panel);
        }
    }

    // ======================================================================
    //  Actualités
    // ======================================================================

    internal static class News
    {
        private static EasyLifeTablettePlugin T { get { return EasyLifeTablettePlugin.Instance; } }
        public static readonly string[] Categories = { "Info", "Mairie", "Événement", "Faits divers", "Économie", "Sport", "Annonce" };

        private static bool CanWrite(Player player)
        {
            return player.IsAdmin || T.Store.ProfileOf(player).Journalist;
        }

        public static void Home(Player player)
        {
            List<Article> articles = T.Store.Data.News.OrderByDescending(a => a.Pinned).ThenByDescending(a => a.Date).Take(50).ToList();
            UIPanel panel = EasyLifeTablettePlugin.Tab("Actualités");
            if (CanWrite(player))
                panel.AddTabLine(Fmt.Color("+ Publier un article", "#0a84ff"), EasyLifeTablettePlugin.Go(player, () => Write(player, null, null)));
            foreach (Article a in articles)
            {
                Article article = a;
                panel.AddTabLine((a.Pinned ? Fmt.Color("À LA UNE  ", "#ff453a") : "") + Fmt.Color(a.Category.ToUpperInvariant(), "#bf5af2") + "  <b>" + Fmt.Cut(a.Title, 50) + "</b>  " + Fmt.Grey(Fmt.Ago(a.Date)),
                    EasyLifeTablettePlugin.Go(player, () => Read(player, article)));
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, articles.Count > 0 || CanWrite(player), () => T.Home(player), "Lire");
        }

        private static void Read(Player player, Article a)
        {
            string text = Fmt.Color(a.Category.ToUpperInvariant(), "#bf5af2") + "\n<b>" + a.Title + "</b>\n" +
                          Fmt.Grey("Par " + a.AuthorName + " · " + a.Date.ToString("dd/MM/yyyy HH:mm")) + "\n\n" + a.Body;
            UIPanel panel = new UIPanel("Actualités", UIPanel.PanelType.Text).SetText(text);
            if (player.IsAdmin)
                panel.AddButton(a.Pinned ? "Retirer de la une" : "Mettre à la une", EasyLifeTablettePlugin.Go(player, () =>
                {
                    a.Pinned = !a.Pinned;
                    T.Store.Save();
                    Read(player, a);
                }));
            if (player.IsAdmin || a.Author == player.steamId.ToString())
                panel.AddButton("Supprimer", EasyLifeTablettePlugin.Go(player, () =>
                {
                    T.Store.Data.News.Remove(a);
                    T.Store.Save();
                    EasyLifeTablettePlugin.Success(player, "Article supprimé.");
                    Home(player);
                }));
            panel.AddButton("Retour", EasyLifeTablettePlugin.Go(player, () => Home(player)));
            player.ShowPanelUI(panel);
        }

        private static void Write(Player player, string title, string category)
        {
            Action back = () => Home(player);
            if (title == null)
            {
                EasyLifeTablettePlugin.Ask(player, "Nouvel article", "Titre de l'article :", "Titre", t =>
                {
                    if (t.Length < 3) { EasyLifeTablettePlugin.Error(player, "Titre trop court."); Write(player, null, null); return; }
                    Write(player, Fmt.Cut(t, 100), null);
                }, back);
                return;
            }
            if (category == null)
            {
                UIPanel panel = EasyLifeTablettePlugin.Tab("Rubrique de l'article");
                foreach (string c in Categories)
                {
                    string cat = c;
                    panel.AddTabLine(c, EasyLifeTablettePlugin.Go(player, () => Write(player, title, cat)));
                }
                EasyLifeTablettePlugin.ListButtons(player, panel, true, back, "Choisir");
                return;
            }
            EasyLifeTablettePlugin.Ask(player, "Nouvel article", "<b>" + title + "</b> (" + category + ")\n\nTexte de l'article :", "Texte", body =>
            {
                if (body.Length < 3) { EasyLifeTablettePlugin.Error(player, "Article trop court."); Write(player, title, category); return; }
                Profile me = T.Store.ProfileOf(player);
                T.Store.Data.News.Add(new Article
                {
                    Id = T.Store.NextId(),
                    Title = title,
                    Category = category,
                    Body = Fmt.Cut(body, 2000),
                    Author = me.SteamId,
                    AuthorName = me.Name,
                    Date = DateTime.Now,
                });
                T.Store.Save();
                EasyLifeTablettePlugin.Success(player, "Article publié !");
                foreach (Player p in EasyLifeTablettePlugin.OnlinePlayers())
                    if (p != player) p.Notify("Actualités", title, NotificationManager.Type.Info, 6f);
                Home(player);
            }, back);
        }
    }

    // ======================================================================
    //  Le Bon Plan
    // ======================================================================

    internal static class Ads
    {
        private static EasyLifeTablettePlugin T { get { return EasyLifeTablettePlugin.Instance; } }
        public static readonly string[] Categories = { "Véhicules", "Immobilier", "Emploi", "Services", "Maison & jardin", "Mode", "Multimédia", "Loisirs", "Matériel professionnel", "Autres" };

        public static void Home(Player player)
        {
            List<Ad> active = T.Store.Data.Ads.Where(a => a.IsActive).ToList();
            Profile me = T.Store.ProfileOf(player);
            UIPanel panel = EasyLifeTablettePlugin.Tab("Le Bon Plan  •  petites annonces de la ville");
            panel.AddTabLine(Fmt.Color("+ Déposer une annonce", "#ff6b1a"), EasyLifeTablettePlugin.Go(player, () => Create(player, null, null, null)));
            panel.AddTabLine("<b>Toutes les annonces</b>  " + Fmt.Grey(active.Count.ToString()), EasyLifeTablettePlugin.Go(player, () => List(player, "all", null)));
            panel.AddTabLine("Mes annonces  " + Fmt.Grey(T.Store.Data.Ads.Count(a => a.Owner == me.SteamId).ToString()), EasyLifeTablettePlugin.Go(player, () => List(player, "mine", null)));
            panel.AddTabLine("Favoris  " + Fmt.Grey(me.Favorites.Count.ToString()), EasyLifeTablettePlugin.Go(player, () => List(player, "favs", null)));
            foreach (string c in Categories)
            {
                string cat = c;
                panel.AddTabLine("   " + c + "  " + Fmt.Grey(active.Count(a => a.Category == c).ToString()), EasyLifeTablettePlugin.Go(player, () => List(player, "cat", cat)));
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, true, () => T.Home(player));
        }

        private static void List(Player player, string view, string category)
        {
            Profile me = T.Store.ProfileOf(player);
            IEnumerable<Ad> ads = T.Store.Data.Ads;
            string title = "Toutes les annonces";
            if (view == "mine") { ads = ads.Where(a => a.Owner == me.SteamId); title = "Mes annonces"; }
            else if (view == "favs") { ads = ads.Where(a => me.Favorites.Contains(a.Id) && a.IsActive); title = "Favoris"; }
            else if (view == "cat") { ads = ads.Where(a => a.IsActive && a.Category == category); title = category; }
            else ads = ads.Where(a => a.IsActive);
            List<Ad> list = ads.OrderByDescending(a => a.Date).Take(80).ToList();

            UIPanel panel = EasyLifeTablettePlugin.Tab("Le Bon Plan  •  " + title);
            foreach (Ad a in list)
            {
                Ad ad = a;
                panel.AddTabLine("<b>" + Fmt.Cut(a.Title, 40) + "</b>  " + Fmt.Color(Fmt.Price(a.Price), "#ff6b1a") + "  " +
                                 Fmt.Grey(a.Category + " · " + Fmt.Ago(a.Date)) + (a.IsActive ? "" : Fmt.Color("  (hors ligne)", "#ff453a")),
                    EasyLifeTablettePlugin.Go(player, () => Detail(player, ad, () => List(player, view, category))));
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, list.Count > 0, () => Home(player), "Voir");
        }

        private static void Detail(Player player, Ad a, Action back)
        {
            Profile me = T.Store.ProfileOf(player);
            bool mine = a.Owner == me.SteamId;
            if (!mine) { a.Views++; T.Store.Save(); }
            Profile seller;
            T.Store.Data.Profiles.TryGetValue(a.Owner, out seller);
            bool sellerOnline = EasyLifeTablettePlugin.FindOnline(a.Owner) != null;

            string text = Fmt.Grey(a.Category.ToUpperInvariant()) + "\n<b>" + a.Title + "</b>\n" +
                          Fmt.Color("<size=24>" + Fmt.Price(a.Price) + "</size>", "#ff6b1a") + "\n\n" +
                          a.Description + "\n\n" +
                          "<b>Vendeur :</b> " + a.OwnerName + (sellerOnline ? Fmt.Color("  ● en ville", "#30d158") : Fmt.Grey("  hors ligne")) +
                          (seller != null && !string.IsNullOrEmpty(seller.Phone) ? "\n<b>Téléphone :</b> " + seller.Phone : "") + "\n" +
                          Fmt.Grey("Publiée le " + a.Date.ToString("dd/MM/yyyy HH:mm") + " · expire le " + a.Expires.ToString("dd/MM/yyyy") + " · " + a.Views + " vue(s)") +
                          (a.IsActive ? "" : "\n" + Fmt.Color("Cette annonce n'est plus en ligne.", "#ff453a")) +
                          "\n\n" + Fmt.Grey("Ne payez jamais d'avance sans avoir vu le bien.");

            UIPanel panel = new UIPanel("Le Bon Plan", UIPanel.PanelType.Text).SetText(text);
            Action self = () => Detail(player, a, back);
            if (!mine && a.IsActive)
            {
                panel.AddButton("Contacter", EasyLifeTablettePlugin.Go(player, () =>
                    Mail.Compose(player, new List<string> { a.Owner }, "Le Bon Plan : " + Fmt.Cut(a.Title, 50), self)));
                bool fav = me.Favorites.Contains(a.Id);
                panel.AddButton(fav ? "Retirer des favoris" : "Ajouter aux favoris", EasyLifeTablettePlugin.Go(player, () =>
                {
                    if (fav) me.Favorites.Remove(a.Id); else me.Favorites.Add(a.Id);
                    T.Store.Save();
                    self();
                }));
            }
            if (mine)
            {
                panel.AddButton(a.IsActive ? "Clôturer (vendu)" : "Remettre en ligne", EasyLifeTablettePlugin.Go(player, () =>
                {
                    if (a.IsActive) a.Closed = true;
                    else { a.Closed = false; a.Expires = DateTime.Now.AddDays(30); }
                    T.Store.Save();
                    self();
                }));
            }
            if (mine || player.IsAdmin)
            {
                panel.AddButton("Supprimer", EasyLifeTablettePlugin.Go(player, () =>
                {
                    T.Store.Data.Ads.Remove(a);
                    if (!mine) T.Notify(a.Owner, "bonplan", "Le Bon Plan", "Votre annonce « " + a.Title + " » a été retirée par la modération.");
                    T.Store.Save();
                    EasyLifeTablettePlugin.Success(player, "Annonce supprimée.");
                    Home(player);
                }));
            }
            panel.AddButton("Retour", EasyLifeTablettePlugin.Go(player, back));
            player.ShowPanelUI(panel);
        }

        /// <summary>Dépôt d'une annonce, étape par étape : titre, catégorie, prix, description.</summary>
        private static void Create(Player player, string title, string category, double? price, bool priceAsked = false)
        {
            Action back = () => Home(player);
            Profile me = T.Store.ProfileOf(player);
            if (T.Store.Data.Ads.Count(a => a.Owner == me.SteamId && a.IsActive) >= T.Config.MaxAdsPerPlayer)
            {
                EasyLifeTablettePlugin.Error(player, "Vous avez déjà " + T.Config.MaxAdsPerPlayer + " annonces en ligne.");
                back();
                return;
            }
            if (title == null)
            {
                EasyLifeTablettePlugin.Ask(player, "Déposer une annonce", "Titre de l'annonce :", "Ex : Clio 4 très bon état", t =>
                {
                    if (t.Length < 3) { EasyLifeTablettePlugin.Error(player, "Titre trop court."); Create(player, null, null, null); return; }
                    Create(player, Fmt.Cut(t, 60), null, null);
                }, back);
                return;
            }
            if (category == null)
            {
                UIPanel panel = EasyLifeTablettePlugin.Tab("Catégorie de l'annonce");
                foreach (string c in Categories)
                {
                    string cat = c;
                    panel.AddTabLine(c, EasyLifeTablettePlugin.Go(player, () => Create(player, title, cat, null)));
                }
                EasyLifeTablettePlugin.ListButtons(player, panel, true, back, "Choisir");
                return;
            }
            if (!priceAsked)
            {
                EasyLifeTablettePlugin.Ask(player, "Déposer une annonce", "Prix en € (laissez vide pour « Prix sur demande ») :", "Ex : 5000", p =>
                {
                    if (string.IsNullOrEmpty(p)) { Create(player, title, category, null, true); return; }
                    double value;
                    if (!Fmt.TryAmount(p, out value)) { EasyLifeTablettePlugin.Error(player, "Prix invalide."); Create(player, title, category, null); return; }
                    Create(player, title, category, value, true);
                }, back);
                return;
            }
            EasyLifeTablettePlugin.Ask(player, "Déposer une annonce", "<b>" + title + "</b> · " + category + " · " + Fmt.Price(price) + "\n\nDescription :", "État, disponibilité, lieu…", d =>
            {
                if (d.Length < 5) { EasyLifeTablettePlugin.Error(player, "Description trop courte."); Create(player, title, category, price, true); return; }
                Ad ad = new Ad
                {
                    Id = T.Store.NextId(),
                    Owner = me.SteamId,
                    OwnerName = me.Name,
                    Title = title,
                    Category = category,
                    Price = price,
                    Description = Fmt.Cut(d, 800),
                    Date = DateTime.Now,
                    Expires = DateTime.Now.AddDays(30),
                };
                T.Store.Data.Ads.Add(ad);
                T.Store.Save();
                EasyLifeTablettePlugin.Success(player, "Annonce publiée pour 30 jours !");
                Detail(player, ad, back);
            }, back);
        }
    }

    // ======================================================================
    //  Commander (restaurants = entreprises du serveur qui ont une carte)
    // ======================================================================

    internal static class Food
    {
        private static EasyLifeTablettePlugin T { get { return EasyLifeTablettePlugin.Instance; } }
        private static readonly string[] Steps = { "Reçue", "En préparation", "Prête", "En livraison", "Terminée", "Annulée" };

        private static Bizs Biz(int id)
        {
            return Nova.biz != null && Nova.biz.bizs != null ? Nova.biz.bizs.FirstOrDefault(b => b != null && b.Id == id) : null;
        }

        private static bool IsMember(Player player, int bizId)
        {
            return player.HasBiz && player.biz != null && player.biz.Id == bizId;
        }

        private static bool IsManager(Player player, int bizId)
        {
            if (player.IsAdmin) return true;
            Bizs biz = Biz(bizId);
            return biz != null && IsMember(player, bizId) && biz.OwnerId == player.character.Id;
        }

        private static bool IsOpen(int bizId)
        {
            return T.DutyOf(bizId).Any(id => EasyLifeTablettePlugin.FindOnline(id) != null);
        }

        public static void Home(Player player)
        {
            List<int> restaurants = T.Store.Data.Menu.Select(i => i.BizId).Distinct().Where(id => Biz(id) != null).ToList();
            Profile me = T.Store.ProfileOf(player);
            UIPanel panel = EasyLifeTablettePlugin.Tab("Commander  •  restaurants de la ville");
            foreach (int id in restaurants.OrderBy(id => Biz(id).BizName))
            {
                int bizId = id;
                panel.AddTabLine("<b>" + Biz(id).BizName + "</b>  " + (IsOpen(id) ? Fmt.Color("● Ouvert", "#30d158") : Fmt.Color("● Fermé", "#ff453a")),
                    EasyLifeTablettePlugin.Go(player, () => Menu(player, bizId, null)));
            }
            int pending = T.Store.Data.Orders.Count(o => o.Customer == me.SteamId && o.Status < 4);
            panel.AddTabLine("Mes commandes" + Fmt.Badge(pending, "en cours"), EasyLifeTablettePlugin.Go(player, () => MyOrders(player)));
            if (player.HasBiz && player.biz != null)
                panel.AddTabLine(Fmt.Color("Espace équipe : " + player.biz.BizName, "#ffd60a"), EasyLifeTablettePlugin.Go(player, () => Team(player)));
            EasyLifeTablettePlugin.ListButtons(player, panel, true, () => T.Home(player));
        }

        // ---------------- clients

        private static void Menu(Player player, int bizId, string category)
        {
            Bizs biz = Biz(bizId);
            if (biz == null) { Home(player); return; }
            Cart cart = T.CartOf(player);
            if (cart.BizId != bizId) { cart.BizId = bizId; cart.Lines.Clear(); }
            List<MenuItem> items = T.Store.Data.Menu.Where(i => i.BizId == bizId && i.Available).ToList();
            bool open = IsOpen(bizId);

            UIPanel panel = EasyLifeTablettePlugin.Tab(biz.BizName + "  •  " + (open ? "Ouvert" : "Fermé : aucun équipier en service") +
                                                        (cart.Count > 0 ? "  •  Panier : " + cart.Count + " (" + Fmt.Money(cart.Total) + ")" : ""));
            foreach (MenuItem i in items.OrderByDescending(i => i.Featured).ThenBy(i => i.Category).ThenBy(i => i.Name))
            {
                MenuItem item = i;
                panel.AddTabLine((i.Featured ? Fmt.Color("À LA UNE  ", "#ffd60a") : "") + Fmt.Grey(i.Category + "  ") + "<b>" + i.Name + "</b>  " +
                                 Fmt.Money(i.Price) + (string.IsNullOrEmpty(i.Description) ? "" : Fmt.Grey("  " + Fmt.Cut(i.Description, 40))), p =>
                {
                    if (!open) { EasyLifeTablettePlugin.Error(player, "Le restaurant est fermé."); return; }
                    cart.Add(item);
                    EasyLifeTablettePlugin.Success(player, item.Name + " ajouté au panier (" + cart.Count + " article(s), " + Fmt.Money(cart.Total) + ").");
                    player.ClosePanel(p);
                    Menu(player, bizId, category);
                });
            }
            if (items.Count == 0) panel.AddTabLine(Fmt.Grey("La carte est vide."), p => { });
            else panel.AddButton("Ajouter", p => p.SelectTab());
            if (cart.Count > 0) panel.AddButton("Panier (" + cart.Count + ")", EasyLifeTablettePlugin.Go(player, () => Checkout(player, bizId)));
            panel.AddButton("Retour", EasyLifeTablettePlugin.Go(player, () => Home(player)));
            panel.AddButton("Fermer", p => player.ClosePanel(p));
            player.ShowPanelUI(panel);
        }

        private static void Checkout(Player player, int bizId)
        {
            Cart cart = T.CartOf(player);
            Bizs biz = Biz(bizId);
            if (biz == null || cart.Count == 0) { Home(player); return; }
            StringBuilder text = new StringBuilder("<b>Panier — " + biz.BizName + "</b>\n\n");
            foreach (OrderLine l in cart.Lines)
                text.Append(l.Qty).Append(" × ").Append(l.Name).Append("   ").Append(Fmt.Money(l.Price * l.Qty)).Append('\n');
            text.Append("\n<b>Total : ").Append(Fmt.Money(cart.Total)).Append("</b>")
                .Append(T.Config.DeliveryFee > 0 ? Fmt.Grey("  (+" + Fmt.Money(T.Config.DeliveryFee) + " en livraison)") : "")
                .Append("\n\n").Append(Fmt.Grey("Payé par votre compte bancaire (solde : " + Fmt.Money(player.Bank) + "). Remboursé si la commande est annulée."));

            UIPanel panel = new UIPanel("Panier", UIPanel.PanelType.Text).SetText(text.ToString());
            panel.AddButton("Au comptoir", EasyLifeTablettePlugin.Go(player, () => Place(player, bizId, "Comptoir", "")));
            panel.AddButton("En livraison", EasyLifeTablettePlugin.Go(player, () =>
                EasyLifeTablettePlugin.Ask(player, "Livraison", "Adresse de livraison :", "Rue, bâtiment, appartement…", address =>
                {
                    if (address.Length < 3) { EasyLifeTablettePlugin.Error(player, "Adresse trop courte."); Checkout(player, bizId); return; }
                    Place(player, bizId, "Livraison", Fmt.Cut(address, 100));
                }, () => Checkout(player, bizId))));
            panel.AddButton("Vider", EasyLifeTablettePlugin.Go(player, () => { cart.Lines.Clear(); Menu(player, bizId, null); }));
            panel.AddButton("Retour", EasyLifeTablettePlugin.Go(player, () => Menu(player, bizId, null)));
            player.ShowPanelUI(panel);
        }

        private static void Place(Player player, int bizId, string mode, string address)
        {
            Cart cart = T.CartOf(player);
            Bizs biz = Biz(bizId);
            if (biz == null || cart.Count == 0) { Home(player); return; }
            if (!IsOpen(bizId)) { EasyLifeTablettePlugin.Error(player, "Le restaurant vient de fermer."); Menu(player, bizId, null); return; }
            double total = cart.Total + (mode == "Livraison" ? T.Config.DeliveryFee : 0);
            if (player.Bank < total) { EasyLifeTablettePlugin.Error(player, "Solde bancaire insuffisant (" + Fmt.Money(player.Bank) + ")."); Checkout(player, bizId); return; }

            Profile me = T.Store.ProfileOf(player);
            Order order = new Order
            {
                Id = T.Store.NextId(),
                BizId = bizId,
                BizName = biz.BizName,
                Customer = me.SteamId,
                CustomerName = me.Name,
                Lines = cart.Lines.Select(l => new OrderLine { Name = l.Name, Qty = l.Qty, Price = l.Price }).ToList(),
                Total = total,
                Mode = mode,
                Address = address,
                Date = DateTime.Now,
            };
            player.AddBankMoney(-total, "Commande " + biz.BizName + " #" + order.Id);
            T.Store.Data.Orders.Add(order);
            cart.Lines.Clear();
            T.Store.Save();

            foreach (string id in T.DutyOf(bizId))
                T.Notify(id, "commander", biz.BizName + " : commande #" + order.Id, me.Name + " — " + Fmt.Money(total) + " (" + mode + ")");
            EasyLifeTablettePlugin.Success(player, "Commande #" + order.Id + " envoyée à " + biz.BizName + " (" + Fmt.Money(total) + ").");
            MyOrders(player);
        }

        private static void MyOrders(Player player)
        {
            Profile me = T.Store.ProfileOf(player);
            List<Order> orders = T.Store.Data.Orders.Where(o => o.Customer == me.SteamId).OrderByDescending(o => o.Date).Take(30).ToList();
            UIPanel panel = EasyLifeTablettePlugin.Tab("Mes commandes");
            foreach (Order o in orders)
            {
                Order order = o;
                panel.AddTabLine("#" + o.Id + "  <b>" + o.BizName + "</b>  " + Fmt.Money(o.Total) + "  " + StatusLabel(o) + "  " + Fmt.Grey(Fmt.Ago(o.Date)),
                    EasyLifeTablettePlugin.Go(player, () => OrderDetail(player, order, false)));
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, orders.Count > 0, () => Home(player), "Voir");
        }

        private static string StatusLabel(Order o)
        {
            string color = o.Status == 5 ? "#ff453a" : o.Status == 4 ? "#98989f" : o.Status == 2 ? "#30d158" : "#ff9f0a";
            return Fmt.Color(Steps[o.Status], color);
        }

        private static void OrderDetail(Player player, Order o, bool staff)
        {
            StringBuilder text = new StringBuilder();
            text.Append("<b>Commande #").Append(o.Id).Append(" — ").Append(o.BizName).Append("</b>\n")
                .Append("Statut : ").Append(StatusLabel(o)).Append('\n')
                .Append("Client : ").Append(o.CustomerName).Append('\n')
                .Append(o.Mode == "Livraison" ? "Livraison : " + o.Address : "À récupérer au comptoir").Append('\n')
                .Append(Fmt.Grey(o.Date.ToString("dd/MM/yyyy HH:mm"))).Append("\n\n");
            foreach (OrderLine l in o.Lines) text.Append(l.Qty).Append(" × ").Append(l.Name).Append("   ").Append(Fmt.Money(l.Price * l.Qty)).Append('\n');
            text.Append("\n<b>Total payé : ").Append(Fmt.Money(o.Total)).Append("</b>");

            UIPanel panel = new UIPanel("Commande #" + o.Id, UIPanel.PanelType.Text).SetText(text.ToString());
            Action back = staff ? (Action)(() => Team(player)) : () => MyOrders(player);
            if (staff && o.Status < 4)
            {
                if (o.Status == 0) panel.AddButton("En préparation", EasyLifeTablettePlugin.Go(player, () => SetStatus(player, o, 1)));
                if (o.Status == 1) panel.AddButton("Prête", EasyLifeTablettePlugin.Go(player, () => SetStatus(player, o, 2)));
                if (o.Status == 2 && o.Mode == "Livraison") panel.AddButton("En livraison", EasyLifeTablettePlugin.Go(player, () => SetStatus(player, o, 3)));
                if (o.Status >= 1) panel.AddButton("Terminée", EasyLifeTablettePlugin.Go(player, () => SetStatus(player, o, 4)));
                panel.AddButton("Annuler", EasyLifeTablettePlugin.Go(player, () => SetStatus(player, o, 5)));
            }
            if (!staff && o.Status == 0)
                panel.AddButton("Annuler", EasyLifeTablettePlugin.Go(player, () => SetStatus(player, o, 5)));
            panel.AddButton("Retour", EasyLifeTablettePlugin.Go(player, back));
            player.ShowPanelUI(panel);
        }

        /// <summary>Change le statut. Terminée = l'argent part à l'entreprise ; Annulée = le client est remboursé.</summary>
        private static void SetStatus(Player player, Order o, int status)
        {
            bool isCustomer = o.Customer == player.steamId.ToString();
            bool staff = IsMember(player, o.BizId) || (player.IsAdmin && !isCustomer);
            if (o.Status >= 4 || (!staff && !(isCustomer && status == 5 && o.Status == 0)))
            {
                OrderDetail(player, o, staff);
                return;
            }
            o.Status = status;
            if (status == 4)
            {
                Bizs biz = Biz(o.BizId);
                if (biz != null) biz.AddBankMoney(o.Total, "Commande tablette #" + o.Id);
            }
            else if (status == 5)
            {
                T.PayPlayer(o.Customer, o.Total, "Remboursement commande #" + o.Id);
            }
            T.Store.Save();
            if (!isCustomer)
                T.Notify(o.Customer, "commander", o.BizName + " : commande #" + o.Id, "Votre commande est maintenant : " + Steps[status] + (status == 5 ? " (remboursée)" : "") + ".");
            else
                foreach (string id in T.DutyOf(o.BizId))
                    T.Notify(id, "commander", o.BizName + " : commande #" + o.Id, o.CustomerName + " a annulé sa commande.");
            OrderDetail(player, o, staff);
        }

        // ---------------- équipe

        private static void Team(Player player)
        {
            if (!player.HasBiz || player.biz == null) { Home(player); return; }
            int bizId = player.biz.Id;
            string me = player.steamId.ToString();
            bool duty = T.DutyOf(bizId).Contains(me);
            List<Order> orders = T.Store.Data.Orders.Where(o => o.BizId == bizId && (o.Status < 4 || (DateTime.Now - o.Date).TotalHours < 12))
                                                    .OrderBy(o => o.Status >= 4).ThenByDescending(o => o.Date).Take(40).ToList();

            UIPanel panel = EasyLifeTablettePlugin.Tab("Équipe " + player.biz.BizName + "  •  " + T.DutyOf(bizId).Count + " en service");
            panel.AddTabLine(duty ? Fmt.Color("Quitter le service", "#ff453a") : Fmt.Color("Prendre le service (ouvre le restaurant)", "#30d158"), p =>
            {
                if (duty) T.DutyOf(bizId).Remove(me); else T.DutyOf(bizId).Add(me);
                EasyLifeTablettePlugin.Success(player, duty ? "Fin de service." : "Vous êtes en service : les clients peuvent commander.");
                player.ClosePanel(p);
                Team(player);
            });
            if (IsManager(player, bizId))
                panel.AddTabLine(Fmt.Color("Gérer la carte", "#ffd60a"), EasyLifeTablettePlugin.Go(player, () => MenuAdmin(player, bizId)));
            foreach (Order o in orders)
            {
                Order order = o;
                panel.AddTabLine("#" + o.Id + "  " + StatusLabel(o) + "  <b>" + o.CustomerName + "</b>  " + Fmt.Money(o.Total) + "  " + Fmt.Grey(o.Mode + " · " + Fmt.Ago(o.Date)),
                    EasyLifeTablettePlugin.Go(player, () => OrderDetail(player, order, true)));
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, true, () => Home(player));
        }

        private static void MenuAdmin(Player player, int bizId)
        {
            List<MenuItem> items = T.Store.Data.Menu.Where(i => i.BizId == bizId).OrderBy(i => i.Category).ThenBy(i => i.Name).ToList();
            UIPanel panel = EasyLifeTablettePlugin.Tab("Carte de " + (Biz(bizId) != null ? Biz(bizId).BizName : "?"));
            panel.AddTabLine(Fmt.Color("+ Ajouter un produit", "#0a84ff"), EasyLifeTablettePlugin.Go(player, () => NewItem(player, bizId, null, null, null)));
            foreach (MenuItem i in items)
            {
                MenuItem item = i;
                panel.AddTabLine(Fmt.Grey(i.Category + "  ") + "<b>" + i.Name + "</b>  " + Fmt.Money(i.Price) +
                                 (i.Featured ? Fmt.Color("  à la une", "#ffd60a") : "") + (i.Available ? "" : Fmt.Color("  en rupture", "#ff453a")),
                    EasyLifeTablettePlugin.Go(player, () => ItemAdmin(player, item)));
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, true, () => Team(player));
        }

        private static void ItemAdmin(Player player, MenuItem item)
        {
            UIPanel panel = new UIPanel(item.Name, UIPanel.PanelType.Text).SetText(
                "<b>" + item.Name + "</b>  " + Fmt.Money(item.Price) + "\n" + Fmt.Grey(item.Category) + "\n" + item.Description);
            Action self = () => ItemAdmin(player, item);
            panel.AddButton(item.Available ? "Mettre en rupture" : "Remettre en vente", EasyLifeTablettePlugin.Go(player, () => { item.Available = !item.Available; T.Store.Save(); self(); }));
            panel.AddButton(item.Featured ? "Retirer de la une" : "Mettre à la une", EasyLifeTablettePlugin.Go(player, () => { item.Featured = !item.Featured; T.Store.Save(); self(); }));
            panel.AddButton("Changer le prix", EasyLifeTablettePlugin.Go(player, () =>
                EasyLifeTablettePlugin.Ask(player, item.Name, "Nouveau prix en € :", Fmt.Money(item.Price), v =>
                {
                    double price;
                    if (Fmt.TryAmount(v, out price)) { item.Price = price; T.Store.Save(); }
                    else EasyLifeTablettePlugin.Error(player, "Prix invalide.");
                    self();
                }, self)));
            panel.AddButton("Supprimer", EasyLifeTablettePlugin.Go(player, () => { T.Store.Data.Menu.Remove(item); T.Store.Save(); MenuAdmin(player, item.BizId); }));
            panel.AddButton("Retour", EasyLifeTablettePlugin.Go(player, () => MenuAdmin(player, item.BizId)));
            player.ShowPanelUI(panel);
        }

        private static void NewItem(Player player, int bizId, string name, string category, double? price)
        {
            Action back = () => MenuAdmin(player, bizId);
            if (name == null)
            {
                EasyLifeTablettePlugin.Ask(player, "Nouveau produit", "Nom du produit :", "Menu Classique", n =>
                {
                    if (n.Length < 2) { EasyLifeTablettePlugin.Error(player, "Nom trop court."); NewItem(player, bizId, null, null, null); return; }
                    NewItem(player, bizId, Fmt.Cut(n, 40), null, null);
                }, back);
                return;
            }
            if (category == null)
            {
                EasyLifeTablettePlugin.Ask(player, "Nouveau produit", name + "\nCatégorie (Menus, Burgers, Boissons, Desserts…) :", "Menus",
                    c => NewItem(player, bizId, name, string.IsNullOrEmpty(c) ? "Menus" : Fmt.Cut(c, 20), null), back);
                return;
            }
            if (price == null)
            {
                EasyLifeTablettePlugin.Ask(player, "Nouveau produit", name + " (" + category + ")\nPrix en € :", "30", p =>
                {
                    double value;
                    if (!Fmt.TryAmount(p, out value)) { EasyLifeTablettePlugin.Error(player, "Prix invalide."); NewItem(player, bizId, name, category, null); return; }
                    NewItem(player, bizId, name, category, value);
                }, back);
                return;
            }
            EasyLifeTablettePlugin.Ask(player, "Nouveau produit", name + " · " + category + " · " + Fmt.Money(price.Value) + "\nDescription (facultatif) :", "Un burger, des frites et un soda.", d =>
            {
                T.Store.Data.Menu.Add(new MenuItem { Id = T.Store.NextId(), BizId = bizId, Name = name, Category = category, Price = price.Value, Description = Fmt.Cut(d, 80) });
                T.Store.Save();
                EasyLifeTablettePlugin.Success(player, name + " ajouté à la carte.");
                back();
            }, back);
        }
    }

    // ======================================================================
    //  Banque
    // ======================================================================

    internal static class Bank
    {
        private static EasyLifeTablettePlugin T { get { return EasyLifeTablettePlugin.Instance; } }

        public static void Home(Player player)
        {
            string me = player.steamId.ToString();
            List<Transfer> history = T.Store.Data.Transfers.Where(t => t.From == me || t.To == me).OrderByDescending(t => t.Date).Take(8).ToList();
            StringBuilder text = new StringBuilder();
            text.Append("<b>Compte courant</b>\n<size=28>").Append(Fmt.Money(player.Bank)).Append("</size>\n")
                .Append("Liquide : ").Append(Fmt.Money(player.Money)).Append("\n\n<b>Derniers virements</b>\n");
            if (history.Count == 0) text.Append(Fmt.Grey("Aucun virement."));
            foreach (Transfer t in history)
            {
                bool outgoing = t.From == me;
                text.Append(Fmt.Color((outgoing ? "-" : "+") + Fmt.Money(t.Amount), outgoing ? "#ff453a" : "#30d158"))
                    .Append("  ").Append(outgoing ? "à " + t.ToName : "de " + t.FromName)
                    .Append(string.IsNullOrEmpty(t.Label) ? "" : "  « " + t.Label + " »")
                    .Append("  ").Append(Fmt.Grey(t.Date.ToString("dd/MM HH:mm"))).Append('\n');
            }

            UIPanel panel = new UIPanel("Banque", UIPanel.PanelType.Text).SetText(text.ToString());
            panel.AddButton("Faire un virement", EasyLifeTablettePlugin.Go(player, () => PickTarget(player)));
            panel.AddButton("Retour", EasyLifeTablettePlugin.Go(player, () => T.Home(player)));
            panel.AddButton("Fermer", p => player.ClosePanel(p));
            player.ShowPanelUI(panel);
        }

        private static void PickTarget(Player player)
        {
            List<Player> online = EasyLifeTablettePlugin.OnlinePlayers().Where(p => p.steamId != player.steamId).OrderBy(p => p.FullName).ToList();
            UIPanel panel = EasyLifeTablettePlugin.Tab("Virement  •  bénéficiaire (joueurs en ville)");
            foreach (Player p in online)
            {
                Player target = p;
                panel.AddTabLine(p.FullName + (string.IsNullOrEmpty(p.character.PhoneNumber) ? "" : Fmt.Grey("  " + p.character.PhoneNumber)),
                    EasyLifeTablettePlugin.Go(player, () => AskAmount(player, target)));
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, online.Count > 0, () => Home(player), "Choisir");
        }

        private static void AskAmount(Player player, Player target)
        {
            EasyLifeTablettePlugin.Ask(player, "Virement à " + target.FullName, "Montant en € (solde : " + Fmt.Money(player.Bank) + ") :", "100", v =>
            {
                double amount;
                if (!Fmt.TryAmount(v, out amount) || amount <= 0) { EasyLifeTablettePlugin.Error(player, "Montant invalide."); AskAmount(player, target); return; }
                if (amount > T.Config.MaxTransfer) { EasyLifeTablettePlugin.Error(player, "Maximum " + Fmt.Money(T.Config.MaxTransfer) + " par virement."); AskAmount(player, target); return; }
                EasyLifeTablettePlugin.Ask(player, "Virement à " + target.FullName, Fmt.Money(amount) + "\nMotif (facultatif) :", "Loyer, achat…",
                    label => Confirm(player, target, amount, Fmt.Cut(label, 40)), () => AskAmount(player, target));
            }, () => PickTarget(player));
        }

        private static void Confirm(Player player, Player target, double amount, string label)
        {
            UIPanel panel = new UIPanel("Confirmer le virement", UIPanel.PanelType.Text).SetText(
                "Envoyer <b>" + Fmt.Money(amount) + "</b> à <b>" + target.FullName + "</b>" + (string.IsNullOrEmpty(label) ? "" : "\nMotif : " + label) +
                "\n\nSolde après virement : " + Fmt.Money(player.Bank - amount));
            panel.AddButton("Confirmer", EasyLifeTablettePlugin.Go(player, () =>
            {
                if (target == null || !target.isInGame) { EasyLifeTablettePlugin.Error(player, "Le bénéficiaire n'est plus en ville."); Home(player); return; }
                if (player.Bank < amount) { EasyLifeTablettePlugin.Error(player, "Solde insuffisant."); Home(player); return; }
                player.AddBankMoney(-amount, "Virement à " + target.FullName);
                target.AddBankMoney(amount, "Virement de " + player.FullName);
                T.Store.Data.Transfers.Add(new Transfer
                {
                    Date = DateTime.Now,
                    From = player.steamId.ToString(),
                    FromName = player.FullName,
                    To = target.steamId.ToString(),
                    ToName = target.FullName,
                    Amount = amount,
                    Label = label,
                });
                T.Store.Save();
                T.Notify(T.Store.ProfileOf(target), "banque", "Virement reçu", Fmt.Money(amount) + " de " + player.FullName + (string.IsNullOrEmpty(label) ? "" : " (« " + label + " »)"));
                EasyLifeTablettePlugin.Success(player, "Virement de " + Fmt.Money(amount) + " envoyé à " + target.FullName + ".");
                Home(player);
            }));
            panel.AddButton("Annuler", EasyLifeTablettePlugin.Go(player, () => Home(player)));
            player.ShowPanelUI(panel);
        }
    }

    // ======================================================================
    //  Notes
    // ======================================================================

    internal static class Notes
    {
        private static EasyLifeTablettePlugin T { get { return EasyLifeTablettePlugin.Instance; } }

        public static void Home(Player player)
        {
            Profile me = T.Store.ProfileOf(player);
            UIPanel panel = EasyLifeTablettePlugin.Tab("Notes");
            panel.AddTabLine(Fmt.Color("+ Nouvelle note", "#0a84ff"), EasyLifeTablettePlugin.Go(player, () => New(player, null)));
            foreach (Note n in me.Notes.OrderByDescending(n => n.Date))
            {
                Note note = n;
                panel.AddTabLine("<b>" + Fmt.Cut(n.Title, 40) + "</b>  " + Fmt.Grey(Fmt.Cut(n.Body, 40)), EasyLifeTablettePlugin.Go(player, () => Read(player, note)));
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, true, () => T.Home(player));
        }

        private static void Read(Player player, Note n)
        {
            UIPanel panel = new UIPanel(n.Title, UIPanel.PanelType.Text).SetText("<b>" + n.Title + "</b>\n" + Fmt.Grey(n.Date.ToString("dd/MM/yyyy HH:mm")) + "\n\n" + n.Body);
            panel.AddButton("Compléter", EasyLifeTablettePlugin.Go(player, () =>
                EasyLifeTablettePlugin.Ask(player, n.Title, "Texte à ajouter à la note :", "…", add =>
                {
                    if (!string.IsNullOrEmpty(add)) { n.Body = Fmt.Cut(n.Body + "\n" + add, 2000); n.Date = DateTime.Now; T.Store.Save(); }
                    Read(player, n);
                }, () => Read(player, n))));
            panel.AddButton("Supprimer", EasyLifeTablettePlugin.Go(player, () => { T.Store.ProfileOf(player).Notes.Remove(n); T.Store.Save(); Home(player); }));
            panel.AddButton("Retour", EasyLifeTablettePlugin.Go(player, () => Home(player)));
            player.ShowPanelUI(panel);
        }

        private static void New(Player player, string title)
        {
            if (title == null)
            {
                EasyLifeTablettePlugin.Ask(player, "Nouvelle note", "Titre :", "Numéros utiles", t => New(player, string.IsNullOrEmpty(t) ? "Sans titre" : Fmt.Cut(t, 40)), () => Home(player));
                return;
            }
            EasyLifeTablettePlugin.Ask(player, "Nouvelle note", "<b>" + title + "</b>\nTexte :", "…", body =>
            {
                Profile me = T.Store.ProfileOf(player);
                if (me.Notes.Count >= 50) { EasyLifeTablettePlugin.Error(player, "50 notes maximum."); Home(player); return; }
                me.Notes.Add(new Note { Title = title, Body = Fmt.Cut(body, 2000), Date = DateTime.Now });
                T.Store.Save();
                Home(player);
            }, () => Home(player));
        }
    }

    // ======================================================================
    //  Réglages
    // ======================================================================

    internal static class Settings
    {
        private static EasyLifeTablettePlugin T { get { return EasyLifeTablettePlugin.Instance; } }

        public static void Home(Player player)
        {
            Profile me = T.Store.ProfileOf(player);
            UIPanel panel = EasyLifeTablettePlugin.Tab("Réglages  •  " + me.Name);
            panel.AddTabLine("Adresse mail : " + Fmt.Grey(me.Mail), p => { });
            panel.AddTabLine("Téléphone : " + Fmt.Grey(string.IsNullOrEmpty(me.Phone) ? "—" : me.Phone), p => { });
            panel.AddTabLine("Code de verrouillage : " + (string.IsNullOrEmpty(me.Pin) ? Fmt.Grey("désactivé") : Fmt.Color("activé", "#30d158")),
                EasyLifeTablettePlugin.Go(player, () => Pin(player)));
            panel.AddTabLine("Notifications à l'écran : " + (me.GameNotifications ? Fmt.Color("oui", "#30d158") : Fmt.Grey("non")), p =>
            {
                me.GameNotifications = !me.GameNotifications;
                T.Store.Save();
                player.ClosePanel(p);
                Home(player);
            });
            if (!string.IsNullOrEmpty(me.Pin))
                panel.AddTabLine("Verrouiller la tablette maintenant", p =>
                {
                    T.Lock(player);
                    player.ClosePanel(p);
                    EasyLifeTablettePlugin.Success(player, "Tablette verrouillée.");
                });
            EasyLifeTablettePlugin.ListButtons(player, panel, true, () => T.Home(player), "Modifier");
        }

        private static void Pin(Player player)
        {
            Profile me = T.Store.ProfileOf(player);
            EasyLifeTablettePlugin.Ask(player, "Code de verrouillage",
                (string.IsNullOrEmpty(me.Pin) ? "" : "Laissez vide pour désactiver le code.\n") + "Nouveau code (4 à 8 chiffres) :", "1234", pin =>
                {
                    if (string.IsNullOrEmpty(pin))
                    {
                        me.Pin = null;
                        EasyLifeTablettePlugin.Success(player, "Code désactivé.");
                    }
                    else if (pin.Length < 4 || pin.Length > 8 || !pin.All(char.IsDigit))
                    {
                        EasyLifeTablettePlugin.Error(player, "Le code doit contenir 4 à 8 chiffres.");
                        Pin(player);
                        return;
                    }
                    else
                    {
                        me.Pin = pin;
                        EasyLifeTablettePlugin.Success(player, "Code enregistré : il sera demandé à chaque connexion.");
                    }
                    T.Store.Save();
                    Home(player);
                }, () => Home(player), true);
        }
    }

    // ======================================================================
    //  Administration (staff)
    // ======================================================================

    internal static class Admin
    {
        private static EasyLifeTablettePlugin T { get { return EasyLifeTablettePlugin.Instance; } }

        public static void Home(Player player)
        {
            if (!player.IsAdmin) { T.Home(player); return; }
            UIPanel panel = EasyLifeTablettePlugin.Tab("Administration");
            panel.AddTabLine("Journalistes (" + T.Store.Data.Profiles.Values.Count(p => p.Journalist) + ")", EasyLifeTablettePlugin.Go(player, () => Journalists(player)));
            panel.AddTabLine("Annuaire : ajouter une fiche (mairie, police…)", EasyLifeTablettePlugin.Go(player, () => NewEntry(player, null, null, null)));
            panel.AddTabLine(Fmt.Grey("Stats : " + T.Store.Data.Profiles.Count + " joueurs · " + T.Store.Data.Mails.Count + " mails · " +
                                      T.Store.Data.Ads.Count(a => a.IsActive) + " annonces · " + T.Store.Data.Orders.Count + " commandes"), p => { });
            EasyLifeTablettePlugin.ListButtons(player, panel, true, () => T.Home(player));
        }

        private static void Journalists(Player player)
        {
            UIPanel panel = EasyLifeTablettePlugin.Tab("Journalistes  •  cliquez pour ajouter / retirer");
            List<Profile> list = T.Store.Data.Profiles.Values.Where(p => p.Journalist).ToList();
            foreach (Player p in EasyLifeTablettePlugin.OnlinePlayers())
            {
                Profile profile = T.Store.ProfileOf(p);
                if (!list.Contains(profile)) list.Add(profile);
            }
            foreach (Profile p in list.OrderByDescending(p => p.Journalist).ThenBy(p => p.Name))
            {
                Profile profile = p;
                panel.AddTabLine((p.Journalist ? Fmt.Color("Journaliste  ", "#bf5af2") : Fmt.Grey("—  ")) + p.Name, x =>
                {
                    profile.Journalist = !profile.Journalist;
                    T.Store.Save();
                    if (profile.Journalist) T.Notify(profile, "actualites", "Actualités", "Vous pouvez maintenant publier des articles.");
                    player.ClosePanel(x);
                    Journalists(player);
                });
            }
            EasyLifeTablettePlugin.ListButtons(player, panel, list.Count > 0, () => Home(player), "Changer");
        }

        private static void NewEntry(Player player, string name, string category, string phone)
        {
            Action back = () => Home(player);
            if (name == null)
            {
                EasyLifeTablettePlugin.Ask(player, "Nouvelle fiche", "Nom :", "Mairie d'Amboise", n =>
                {
                    if (n.Length < 2) { back(); return; }
                    NewEntry(player, Fmt.Cut(n, 40), null, null);
                }, back);
                return;
            }
            if (category == null)
            {
                EasyLifeTablettePlugin.Ask(player, "Nouvelle fiche", name + "\nCatégorie :", "Services publics",
                    c => NewEntry(player, name, string.IsNullOrEmpty(c) ? "Services" : Fmt.Cut(c, 30), null), back);
                return;
            }
            if (phone == null)
            {
                EasyLifeTablettePlugin.Ask(player, "Nouvelle fiche", name + " · " + category + "\nTéléphone :", "17",
                    p => NewEntry(player, name, category, Fmt.Cut(p, 20)), back);
                return;
            }
            EasyLifeTablettePlugin.Ask(player, "Nouvelle fiche", name + " · " + category + " · " + phone + "\nDescription (horaires, adresse…) :", "", d =>
            {
                T.Store.Data.Directory.Add(new DirEntry { Id = T.Store.NextId(), Name = name, Category = category, Phone = phone, Description = Fmt.Cut(d, 300) });
                T.Store.Save();
                EasyLifeTablettePlugin.Success(player, "Fiche ajoutée à l'Annuaire.");
                back();
            }, back);
        }
    }

    // ======================================================================
    //  Mise en forme
    // ======================================================================

    internal static class Fmt
    {
        public static readonly CultureInfo Fr = FrenchCulture();

        private static CultureInfo FrenchCulture()
        {
            try { return new CultureInfo("fr-FR"); }
            catch { return CultureInfo.InvariantCulture; }
        }

        public static string Money(double amount)
        {
            return amount.ToString("#,0.##", CultureInfo.InvariantCulture).Replace(",", " ").Replace(".", ",") + " €";
        }

        public static string Price(double? price)
        {
            return price.HasValue ? Money(price.Value) : "Prix sur demande";
        }

        public static bool TryAmount(string text, out double value)
        {
            string s = (text ?? "").Replace("€", "").Replace(" ", "").Replace(",", ".");
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0 && value < 1e9 && !double.IsNaN(value);
        }

        public static string Color(string text, string hex) { return "<color=" + hex + ">" + text + "</color>"; }
        public static string Grey(string text) { return Color(text, "#98989f"); }
        public static string App(string name) { return "<b>" + name + "</b>"; }

        public static string Badge(int count, string word)
        {
            return count > 0 ? "  " + Color("(" + count + " " + word + (count > 1 ? "s" : "") + ")", "#ff453a") : "";
        }

        public static string Cut(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        /// <summary>Supprime les balises de mise en forme saisies par les joueurs.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("<", "").Replace(">", "").Trim();
        }

        public static string Ago(DateTime date)
        {
            TimeSpan d = DateTime.Now - date;
            if (d.TotalMinutes < 1) return "à l'instant";
            if (d.TotalHours < 1) return "il y a " + (int)d.TotalMinutes + " min";
            if (d.TotalDays < 1) return "il y a " + (int)d.TotalHours + " h";
            if (d.TotalDays < 7) return "il y a " + (int)d.TotalDays + " j";
            return date.ToString("dd/MM/yyyy");
        }
    }

    // ======================================================================
    //  Données (Plugins/EasyLifeTablette/data.json)
    // ======================================================================

    internal class Store
    {
        public StoreData Data = new StoreData();
        private string path;
        private bool dirty;

        public static Store Load(string path)
        {
            Store store = new Store { path = path };
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path))
                    store.Data = JsonConvert.DeserializeObject<StoreData>(File.ReadAllText(path)) ?? new StoreData();
            }
            catch (Exception e)
            {
                // On garde une copie du fichier abîmé au lieu de l'écraser
                Debug.LogError("[EasyLifeTablette] data.json illisible : " + e.Message);
                try { File.Copy(path, path + ".erreur-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch { }
            }
            return store;
        }

        public void Save() { dirty = true; }

        public void Flush()
        {
            if (!dirty) return;
            dirty = false;
            try
            {
                DateTime now = DateTime.Now;
                Data.Ads.RemoveAll(a => (now - a.Expires).TotalDays > 30);
                Data.Orders.RemoveAll(o => (now - o.Date).TotalDays > 14 && o.Status >= 4);
                Data.Transfers.RemoveAll(t => (now - t.Date).TotalDays > 30);
                Data.Mails.RemoveAll(m => m.DeletedBy.Contains(m.From) && m.To.All(m.DeletedBy.Contains));

                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(Data, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                dirty = true;
                Debug.LogError("[EasyLifeTablette] sauvegarde impossible : " + e.Message);
            }
        }

        public int NextId() { Save(); return ++Data.NextId; }

        /// <summary>Profil du joueur (créé à la première utilisation), mis à jour avec son nom et son numéro.</summary>
        public Profile ProfileOf(Player player)
        {
            string id = player.steamId.ToString();
            Profile profile;
            if (!Data.Profiles.TryGetValue(id, out profile))
            {
                profile = new Profile { SteamId = id };
                Data.Profiles[id] = profile;
                Save();
            }
            if (player.character != null)
            {
                string name = player.FullName;
                string phone = player.character.PhoneNumber ?? "";
                if (profile.Name != name || profile.Phone != phone || profile.CharacterId != player.character.Id)
                {
                    profile.Name = name;
                    profile.Phone = phone;
                    profile.CharacterId = player.character.Id;
                    Save();
                }
                if (string.IsNullOrEmpty(profile.Mail)) profile.Mail = NewAddress(player.character.Firstname, player.character.Lastname);
            }
            return profile;
        }

        private string NewAddress(string first, string last)
        {
            string domain = EasyLifeTablettePlugin.Instance.Config.MailDomain;
            string baseName = (Slug(first) + "." + Slug(last)).Trim('.');
            if (baseName.Length == 0) baseName = "joueur";
            string address = baseName + "@" + domain;
            int n = 2;
            while (Data.Profiles.Values.Any(p => p.Mail == address)) address = baseName + (n++) + "@" + domain;
            Save();
            return address;
        }

        private static string Slug(string text)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (text ?? "").Normalize(NormalizationForm.FormD).ToLowerInvariant())
            {
                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9') sb.Append(c);
                else if (c == ' ' || c == '-') sb.Append('-');
            }
            return sb.ToString();
        }

        public string NameOf(string steamId)
        {
            Profile p;
            return Data.Profiles.TryGetValue(steamId, out p) && !string.IsNullOrEmpty(p.Name) ? p.Name : "Inconnu";
        }

        public string MailOf(string steamId)
        {
            Profile p;
            return Data.Profiles.TryGetValue(steamId, out p) ? p.Mail : "";
        }

        public Profile OwnerOf(int bizId)
        {
            Bizs biz = Nova.biz != null && Nova.biz.bizs != null ? Nova.biz.bizs.FirstOrDefault(b => b != null && b.Id == bizId) : null;
            return biz == null ? null : Data.Profiles.Values.FirstOrDefault(p => p.CharacterId == biz.OwnerId);
        }

        public List<MailMessage> Inbox(string steamId)
        {
            return Data.Mails.Where(m => m.To.Contains(steamId) && !m.DeletedBy.Contains(steamId)).OrderByDescending(m => m.Date).ToList();
        }

        public List<MailMessage> Sent(string steamId)
        {
            return Data.Mails.Where(m => m.From == steamId && !m.DeletedBy.Contains(steamId)).OrderByDescending(m => m.Date).ToList();
        }
    }

    internal class StoreData
    {
        public int NextId;
        public Dictionary<string, Profile> Profiles = new Dictionary<string, Profile>();
        public List<MailMessage> Mails = new List<MailMessage>();
        public List<Article> News = new List<Article>();
        public List<Ad> Ads = new List<Ad>();
        public List<MenuItem> Menu = new List<MenuItem>();
        public List<Order> Orders = new List<Order>();
        public List<Transfer> Transfers = new List<Transfer>();
        public List<DirEntry> Directory = new List<DirEntry>();
    }

    internal class Profile
    {
        public string SteamId;
        public string Name = "";
        public string Phone = "";
        public int CharacterId;
        public string Mail = "";
        public string Pin;
        public bool Journalist;
        public bool GameNotifications = true;
        public double PendingBank;
        public List<int> Favorites = new List<int>();
        public List<Note> Notes = new List<Note>();
        public List<Notif> Notifications = new List<Notif>();
    }

    internal class Notif
    {
        public string App;
        public string Title;
        public string Body;
        public DateTime Date;
        public bool Read;
    }

    internal class Note
    {
        public string Title;
        public string Body;
        public DateTime Date;
    }

    internal class MailMessage
    {
        public int Id;
        public string From;
        public string FromName;
        public List<string> To = new List<string>();
        public string Subject;
        public string Body;
        public DateTime Date;
        public List<string> ReadBy = new List<string>();
        public List<string> DeletedBy = new List<string>();
    }

    internal class Article
    {
        public int Id;
        public string Title;
        public string Body;
        public string Category;
        public string Author;
        public string AuthorName;
        public DateTime Date;
        public bool Pinned;
    }

    internal class Ad
    {
        public int Id;
        public string Owner;
        public string OwnerName;
        public string Title;
        public string Description;
        public double? Price;
        public string Category;
        public DateTime Date;
        public DateTime Expires;
        public int Views;
        public bool Closed;

        [JsonIgnore]
        public bool IsActive { get { return !Closed && Expires > DateTime.Now; } }
    }

    internal class MenuItem
    {
        public int Id;
        public int BizId;
        public string Name;
        public string Description;
        public string Category;
        public double Price;
        public bool Featured;
        public bool Available = true;
    }

    internal class OrderLine
    {
        public string Name;
        public int Qty;
        public double Price;
    }

    internal class Order
    {
        public int Id;
        public int BizId;
        public string BizName;
        public string Customer;
        public string CustomerName;
        public List<OrderLine> Lines = new List<OrderLine>();
        public double Total;
        public string Mode;
        public string Address;
        /// <summary>0 Reçue, 1 En préparation, 2 Prête, 3 En livraison, 4 Terminée, 5 Annulée.</summary>
        public int Status;
        public DateTime Date;
    }

    internal class Transfer
    {
        public DateTime Date;
        public string From;
        public string FromName;
        public string To;
        public string ToName;
        public double Amount;
        public string Label;
    }

    internal class DirEntry
    {
        public int Id;
        public int BizId;
        public string Name;
        public string Category;
        public string Phone;
        public string Description;
        public bool Recruiting;
    }

    /// <summary>Panier en mémoire (non sauvegardé).</summary>
    internal class Cart
    {
        public int BizId;
        public List<OrderLine> Lines = new List<OrderLine>();

        public int Count { get { return Lines.Sum(l => l.Qty); } }
        public double Total { get { return Lines.Sum(l => l.Qty * l.Price); } }

        public void Add(MenuItem item)
        {
            OrderLine line = Lines.FirstOrDefault(l => l.Name == item.Name);
            if (line == null) Lines.Add(new OrderLine { Name = item.Name, Qty = 1, Price = item.Price });
            else if (line.Qty < 20) line.Qty++;
        }
    }

    /// <summary>Plugins/EasyLifeTablette/config.json (créé au premier lancement).</summary>
    internal class Config
    {
        public string ServerName = "Easy Life";
        public string MailDomain = "easylife.fr";

        /// <summary>Touche qui ouvre la tablette (« None » = seulement /tablette).</summary>
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public KeyCode Key = KeyCode.None;

        public double MaxTransfer = 1000000;
        public double DeliveryFee = 0;
        public int MaxAdsPerPlayer = 10;

        public static Config Load(string path)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
                Config config = File.Exists(path) ? JsonConvert.DeserializeObject<Config>(File.ReadAllText(path)) ?? new Config() : new Config();
                // Réécrit le fichier pour y ajouter les options manquantes
                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
                return config;
            }
            catch (Exception e)
            {
                Debug.LogError("[EasyLifeTablette] config illisible (" + path + ") : " + e.Message);
                return new Config();
            }
        }
    }
}
