using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.DiscordHelper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using Newtonsoft.Json;
using SQLite;
using static ModKit.Helper.TextFormattingHelper;

namespace LorisCasino
{
    /// <summary>
    /// Loris Casino - Created by Loris Strange.
    /// Casino complet en panels : jeux, jetons, boutique, récompense quotidienne,
    /// espace employé et configuration. Aucune commande : tout passe par AAMenu
    /// et par les points bleus du casino.
    /// </summary>
    public class LorisCasinoPlugin : ModKit.ModKit
    {
        public const string AuthorName = "Loris Strange";
        public const string Credit = "Created by Loris Strange";
        /// <summary>Mise maximale absolue, quelle que soit la configuration.</summary>
        public const int HardMaxBet = 500;

        public static LorisCasinoPlugin Instance { get; private set; }

        private readonly Random _rng = new Random();
        private readonly Dictionary<int, CasinoClient> _clients = new Dictionary<int, CasinoClient>();
        private readonly Dictionary<int, Task<CasinoClient>> _pendingClients = new Dictionary<int, Task<CasinoClient>>();
        private readonly HashSet<int> _staffIds = new HashSet<int>();
        private CasinoConfig _config = new CasinoConfig();
        private bool _configLoaded;
        private bool _menusRegistered;
        private bool _maintenanceRunning;
        private CasinoPoint _pattern;

        public CasinoConfig Cfg => _config;

        public LorisCasinoPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", AuthorName);
            Instance = this;
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            Orm.RegisterTable<CasinoConfig>();
            Orm.RegisterTable<CasinoClient>();
            Orm.RegisterTable<CasinoStaff>();
            Orm.RegisterTable<CasinoPoint>();

            _pattern = new CasinoPoint(false);
            PointHelper.AddPattern(nameof(CasinoPoint), _pattern);

            TryRegisterMenus();
            Nova.server.OnMinutePassedEvent += OnMinutePassed;
            LoadData();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", $"initialisé - {Credit}");
        }

        public override async void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            // Crée les points bleus du casino pour ce joueur
            PointHelper.InitAllNPoint(player);
            try
            {
                // Vérifie / crée le compte casino du joueur
                await GetClient(player);
            }
            catch (Exception e)
            {
                Logger.LogError("LorisCasino", $"Compte casino de {player.FullName} : {e.Message}");
            }
        }

        // ==================================================================
        //  Initialisation
        // ==================================================================

        private void TryRegisterMenus()
        {
            if (_menusRegistered || AAMenu.AAMenu.menu == null) return;
            _menusRegistered = true;

            // Administration > Points bleus > Type de point : Loris Casino
            AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "Loris Casino", _pattern, this);
            // Interactions > Loris Casino (menu principal)
            AAMenu.Menu.AddInteractionTabLine(PluginInformations, "Loris Casino", ui => FromAAMenu(ui, OpenFromInteraction));
            // Administration > Plugins > Loris Casino (configuration, affiché "by Loris Strange")
            AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 0, "Loris Casino", ui => FromAAMenu(ui, p => OpenConfig(p, true)));
        }

        private static void FromAAMenu(UIPanel ui, Action<Player> open)
        {
            Player player = Nova.server.Players.FirstOrDefault(p => p.netId == ui.playerId);
            if (player == null) return;
            player.ClosePanel(ui);
            open(player);
        }

        private async void LoadData()
        {
            Exception last = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(500);
                try
                {
                    CasinoConfig config = (await CasinoConfig.QueryAll()).FirstOrDefault();
                    if (config == null)
                    {
                        config = new CasinoConfig();
                        config.StoreGames();
                        await config.Save();
                    }
                    config.LoadGames();
                    _config = config;

                    _staffIds.Clear();
                    foreach (CasinoStaff staff in await CasinoStaff.QueryAll())
                        _staffIds.Add(staff.CharacterId);

                    _configLoaded = true;
                    Logger.LogSuccess("LorisCasino", $"configuration chargée - {Credit}");
                    return;
                }
                catch (Exception e)
                {
                    last = e;
                }
            }
            Logger.LogError("LorisCasino", $"impossible de charger la configuration : {last?.Message}");
        }

        private async Task<bool> SaveConfig()
        {
            if (!_configLoaded) return false;
            Cfg.StoreGames();
            return await Cfg.Save();
        }

        // ==================================================================
        //  Comptes casino (jetons)
        // ==================================================================

        public Task<CasinoClient> GetClient(Player player)
        {
            int characterId = player.character.Id;
            if (_clients.TryGetValue(characterId, out CasinoClient client)) return Task.FromResult(client);
            if (_pendingClients.TryGetValue(characterId, out Task<CasinoClient> pending)) return pending;

            Task<CasinoClient> task = LoadOrCreateClient(characterId, player.FullName);
            if (!task.IsCompleted) _pendingClients[characterId] = task;
            return task;
        }

        private async Task<CasinoClient> LoadOrCreateClient(int characterId, string name)
        {
            try
            {
                CasinoClient client = (await CasinoClient.Query(c => c.CharacterId == characterId)).FirstOrDefault();
                if (client == null)
                {
                    client = new CasinoClient { CharacterId = characterId, Name = name };
                    await client.Save();
                }
                else if (name != null && client.Name != name)
                {
                    client.Name = name;
                    await client.Save();
                }
                _clients[characterId] = client;
                return client;
            }
            finally
            {
                _pendingClients.Remove(characterId);
            }
        }

        /// <summary>Compte d'un personnage (connecté ou non), sans le créer.</summary>
        private async Task<CasinoClient> FindClient(int characterId)
        {
            if (_clients.TryGetValue(characterId, out CasinoClient cached)) return cached;
            Player online = Nova.server.GetAllInGamePlayers().FirstOrDefault(p => p.character.Id == characterId);
            if (online != null) return await GetClient(online);

            CasinoClient client = (await CasinoClient.Query(c => c.CharacterId == characterId)).FirstOrDefault();
            if (client != null) _clients[characterId] = client;
            return client;
        }

        // ==================================================================
        //  Permissions
        // ==================================================================

        public bool IsCasinoAdmin(Player player)
        {
            return player.IsAdmin && player.serviceAdmin && player.account.AdminLevel >= Cfg.AdminLevel;
        }

        private bool IsCasinoOwner(Player player)
        {
            if (Cfg.CasinoBizId <= 0 || !player.HasBiz || player.character.BizId != Cfg.CasinoBizId) return false;
            Bizs biz = player.biz;
            return biz != null && biz.OwnerId == player.character.Id;
        }

        private bool WorksAtCasino(Player player)
        {
            return Cfg.CasinoBizId > 0 && player.character.BizId == Cfg.CasinoBizId;
        }

        /// <summary>Staff casino : admin autorisé, propriétaire de l'entreprise casino ou présent dans CasinoStaff.</summary>
        public bool IsCasinoStaff(Player player)
        {
            return IsCasinoAdmin(player) || IsCasinoOwner(player) || _staffIds.Contains(player.character.Id);
        }

        private bool CanUseEmployeeSpace(Player player) => IsCasinoStaff(player) || WorksAtCasino(player);

        private bool CanManageStaff(Player player) => IsCasinoAdmin(player) || IsCasinoOwner(player);

        // ==================================================================
        //  Outils panels / notifications / Discord
        // ==================================================================

        private PanelHelper Panels => (AAMenu.AAMenu.menu?.Context ?? this).PanelHelper;

        private Panel NewPanel(Player player, string section, UIPanel.PanelType type, Action refresh)
        {
            Panel panel = Panels.Create($"{Cfg.PanelTitle} - {section}", type, player, refresh);
            panel.subtitle = Credit;
            return panel;
        }

        /// <summary>Affiche le panel avec la signature "Created by Loris Strange".</summary>
        private static void Show(Panel panel)
        {
            string credit = Size(Italic(Color(Credit, Colors.Purple)), 14);
            if (panel.type == UIPanel.PanelType.Tab || panel.type == UIPanel.PanelType.TabPrice)
            {
                panel.AddTabLine(credit, _ => { });
            }
            else
            {
                panel.TextLines.Add("");
                panel.TextLines.Add(credit);
            }
            panel.Display();
        }

        /// <summary>Ferme proprement le panel courant (sans dépiler l'historique) avant d'en ouvrir un autre.</summary>
        private static void Leave(Panel panel)
        {
            panel.Player.ClosePanel(panel);
            panel.RemoveFromInstance?.Invoke(panel, EventArgs.Empty);
        }

        /// <summary>Ligne d'onglet qui ouvre un autre panel.</summary>
        private static void Link(Panel panel, string name, Action next)
        {
            panel.AddTabLine(name, _ =>
            {
                Leave(panel);
                next();
            });
        }

        private static void Info(Panel panel, string text) => panel.AddTabLine(text, _ => { });

        private static void SelectButton(Panel panel) => panel.AddButton("Sélectionner", _ => panel.SelectTab());

        public void Notify(Player player, string message, NotificationManager.Type type = NotificationManager.Type.Info)
        {
            player.Notify(Cfg.PanelTitle, message, type, 5f);
        }

        private static string Tokens(long amount) => $"{amount} jeton{(Math.Abs(amount) > 1 ? "s" : "")}";

        private static string Euros(double amount) => amount.ToString("0.##", CultureInfo.GetCultureInfo("fr-FR")) + " €";

        private static string YesNo(bool value) => value ? Color("Oui", Colors.Success) : Color("Non", Colors.Error);

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;

        private static bool TryParseInt(string input, int min, int max, out int value)
        {
            return int.TryParse(input?.Trim(), out value) && value >= min && value <= max;
        }

        private bool ReadAmount(Player player, string input, int min, int max, out int value)
        {
            if (TryParseInt(input, min, max, out value)) return true;
            Notify(player, max >= min ? $"Saisissez un nombre entre {min} et {max}." : "Montant impossible.", NotificationManager.Type.Error);
            return false;
        }

        private void Log(string title, string description, params (string name, string value)[] fields)
        {
            if (!Cfg.WebhookEnabled || string.IsNullOrWhiteSpace(Cfg.WebhookUrl)) return;
            _ = SendWebhook(title, description, fields);
        }

        private async Task SendWebhook(string title, string description, (string name, string value)[] fields)
        {
            try
            {
                DiscordWebhookClient client = new DiscordWebhookClient(Cfg.WebhookUrl);
                await DiscordHelper.SendEmbed(client, Cfg.WebhookColor, $"{Cfg.PanelTitle} • {title}", description,
                    fields.Select(f => f.name).ToList(), fields.Select(f => f.value).ToList(),
                    true, true, $"{Cfg.PanelTitle} • {Credit}");
            }
            catch (Exception e)
            {
                Logger.LogError("LorisCasino", $"Webhook Discord : {e.Message}");
            }
        }

        private void LogStaff(Player staff, string action)
        {
            Log("Action staff", action, ("Staff", $"{staff.FullName} (ID {staff.character.Id})"));
        }

        // ==================================================================
        //  Argent (€) : achats et reventes passent par l'entreprise casino si configurée
        // ==================================================================

        private Bizs CasinoBiz() => Cfg.CasinoBizId > 0 ? Nova.biz.FetchBiz(Cfg.CasinoBizId) : null;

        private bool PayOut(Player target, double euros, string reason)
        {
            if (!target.CanAddMoney(euros))
            {
                Notify(target, "Vous ne pouvez pas recevoir autant d'argent liquide.", NotificationManager.Type.Error);
                return false;
            }
            Bizs biz = CasinoBiz();
            if (biz != null)
            {
                if (biz.Bank < euros)
                {
                    Notify(target, "Le casino n'a pas assez de fonds pour cette conversion.", NotificationManager.Type.Error);
                    return false;
                }
                biz.AddBankMoney(-euros, reason);
            }
            target.AddMoney(euros, reason);
            return true;
        }

        // ==================================================================
        //  Point d'entrée AAMenu / points bleus
        // ==================================================================

        private void OpenFromInteraction(Player player)
        {
            if (Cfg.MenuAnywhere || CanUseEmployeeSpace(player) || IsCasinoAdmin(player))
                OpenMain(player, true);
            else
                Notify(player, "Rendez-vous au casino pour jouer (points bleus).", NotificationManager.Type.Warning);
        }

        public void OnPointTriggered(Player player, int pointType, string gameKey)
        {
            switch ((PointKind)pointType)
            {
                case PointKind.Games:
                    if (!string.IsNullOrEmpty(gameKey) && Enum.TryParse(gameKey, out GameId game))
                        OpenGame(player, game, false);
                    else
                        OpenGamesMenu(player, false);
                    break;
                case PointKind.Shop:
                    OpenShop(player, false);
                    break;
                case PointKind.Daily:
                    _ = ClaimDaily(player);
                    break;
                case PointKind.Employee:
                    OpenEmployeeSpace(player, false);
                    break;
                case PointKind.MainMenu:
                    OpenMain(player, false);
                    break;
            }
        }

        // ==================================================================
        //  Menu principal
        // ==================================================================

        private async void OpenMain(Player player, bool back)
        {
            CasinoClient client = await GetClient(player);
            Panel panel = NewPanel(player, "Menu principal", UIPanel.PanelType.Tab, () => OpenMain(player, back));

            Info(panel, $"{Color("Solde :", Colors.Info)} {Tokens(client.Tokens)}");
            Link(panel, "Jeux", () => OpenGamesMenu(player, true));
            Link(panel, "Boutique", () => OpenShop(player, true));
            panel.AddTabLine($"Récompense quotidienne ({Tokens(Cfg.DailyReward)})", async _ =>
            {
                await ClaimDaily(player);
                panel.Refresh();
            });
            Link(panel, "Classement de la semaine", () => OpenLeaderboard(player));
            if (CanUseEmployeeSpace(player))
                Link(panel, Color("Espace employé", Colors.Orange), () => OpenEmployeeSpace(player, true));
            if (IsCasinoAdmin(player))
                Link(panel, Color("Configuration", Colors.Warning), () => OpenConfig(player, true));

            SelectButton(panel);
            if (back) panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private async Task ClaimDaily(Player player)
        {
            if (Cfg.DailyReward <= 0)
            {
                Notify(player, "La récompense quotidienne est désactivée.", NotificationManager.Type.Warning);
                return;
            }
            CasinoClient client = await GetClient(player);
            DateTime today = DateTime.Now.Date;
            if (client.LastDaily > 0 && FromUnix(client.LastDaily).Date >= today)
            {
                TimeSpan wait = today.AddDays(1) - DateTime.Now;
                Notify(player, $"Récompense déjà récupérée. Revenez dans {wait.Hours}h{wait.Minutes:00}.", NotificationManager.Type.Warning);
                return;
            }
            int reward = Cfg.DailyReward;
            client.LastDaily = Now;
            client.Tokens += reward;
            await client.Save();
            Notify(player, $"Récompense quotidienne : +{Tokens(reward)} !", NotificationManager.Type.Success);
            Log("Récompense quotidienne", $"{player.FullName} a récupéré {Tokens(reward)}.");
        }

        private async void OpenLeaderboard(Player player)
        {
            List<CasinoClient> players = await CasinoClient.Query(c => c.WeeklyBet > 0);
            List<CasinoClient> top = players.OrderByDescending(c => c.WeeklyWon - c.WeeklyBet).Take(10).ToList();

            Panel panel = NewPanel(player, "Classement", UIPanel.PanelType.Text, () => OpenLeaderboard(player));
            panel.TextLines.Add(Bold("Gains nets de la semaine"));
            if (top.Count == 0) panel.TextLines.Add("Personne n'a encore joué cette semaine.");
            for (int i = 0; i < top.Count; i++)
            {
                long net = top[i].WeeklyWon - top[i].WeeklyBet;
                string netText = net >= 0 ? Color($"+{net}", Colors.Success) : Color($"{net}", Colors.Error);
                panel.TextLines.Add($"{i + 1}. {top[i].Name} : {netText} (mises : {top[i].WeeklyBet})");
            }
            panel.TextLines.Add("");
            panel.TextLines.Add($"Prochaine remise à zéro : {NextWeeklyReset():dd/MM/yyyy HH:mm}");
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ==================================================================
        //  Jeux : menu et logique commune
        // ==================================================================

        private static readonly Dictionary<GameId, string> GameNames = new Dictionary<GameId, string>
        {
            { GameId.PileOuFace, "Pile ou Face" },
            { GameId.Roulette, "Roulette" },
            { GameId.MachineASous, "Machine à sous" },
            { GameId.Blackjack, "Blackjack" },
            { GameId.Ticket, "Ticket à gratter" },
            { GameId.De, "Dé - Double ou Rien" },
            { GameId.Coffre, "Coffre Mystère" },
            { GameId.Multiplicateur, "Machine des Multiplicateurs" },
        };

        private static bool IsFixedPrice(GameId game) => game == GameId.Ticket || game == GameId.Coffre;

        private GameSettings Game(GameId game) => Cfg.GetGame(game);

        private static int MaxBet(GameSettings s) => Math.Max(1, Math.Min(s.MaxBet, HardMaxBet));

        private static int MinBet(GameSettings s) => Math.Max(1, Math.Min(s.MinBet, MaxBet(s)));

        private static int Price(GameSettings s) => Math.Max(1, Math.Min(s.Price, HardMaxBet));

        private string BetRange(GameId game)
        {
            GameSettings s = Game(game);
            return IsFixedPrice(game) ? $"prix : {Tokens(Price(s))}" : $"mise : {MinBet(s)} à {MaxBet(s)}";
        }

        private async void OpenGamesMenu(Player player, bool back)
        {
            CasinoClient client = await GetClient(player);
            Panel panel = NewPanel(player, "Jeux", UIPanel.PanelType.Tab, () => OpenGamesMenu(player, back));

            Info(panel, $"{Color("Solde :", Colors.Info)} {Tokens(client.Tokens)}");
            bool any = false;
            foreach (GameId game in GameNames.Keys)
            {
                if (!Game(game).Enabled) continue;
                any = true;
                Link(panel, $"{GameNames[game]} {Color($"({BetRange(game)})", Colors.Grey)}", () => OpenGame(player, game, true));
            }
            if (!any) Info(panel, "Aucun jeu n'est disponible pour le moment.");

            SelectButton(panel);
            if (back) panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private void OpenGame(Player player, GameId game, bool back)
        {
            if (!Game(game).Enabled)
            {
                Notify(player, $"{GameNames[game]} est fermé pour le moment.", NotificationManager.Type.Warning);
                return;
            }
            switch (game)
            {
                case GameId.PileOuFace:
                    OpenBetPanel(player, game, back, new[] { "Gain : x2 (1 chance sur 2)" },
                        ("Pile", bet => PlayCoinFlip(player, bet, true)),
                        ("Face", bet => PlayCoinFlip(player, bet, false)));
                    break;
                case GameId.Roulette:
                    OpenBetPanel(player, game, back, new[]
                        {
                            $"{Color("Rouge", Colors.Error)} : x2 (47,5 %)",
                            $"{Bold("Noir")} : x2 (47,5 %)",
                            $"{Color("Vert", Colors.Success)} : x10 (5 %)",
                        },
                        ("Rouge", bet => PlayRoulette(player, bet, RouletteColor.Rouge)),
                        ("Noir", bet => PlayRoulette(player, bet, RouletteColor.Noir)),
                        ("Vert", bet => PlayRoulette(player, bet, RouletteColor.Vert)));
                    break;
                case GameId.MachineASous:
                    OpenBetPanel(player, game, back, new[]
                        {
                            "3 identiques : Cerise x3, Citron x4, Orange x5,",
                            "Cloche x10, Étoile x20, 7 x50",
                            "2 identiques : x1,5",
                        },
                        ("Jouer", bet => PlaySlots(player, bet)));
                    break;
                case GameId.Blackjack:
                    OpenBetPanel(player, game, back, new[]
                        {
                            "Tirer ou Rester, la banque tire jusqu'à 17.",
                            "Victoire : x2 / Égalité : mise rendue",
                        },
                        ("Distribuer", bet => PlayBlackjack(player, bet)));
                    break;
                case GameId.De:
                    OpenBetPanel(player, game, back, new[] { "Gagné si le dé fait 5 ou 6 : x2" },
                        ("Lancer le dé", bet => PlayDice(player, bet)));
                    break;
                case GameId.Multiplicateur:
                    OpenBetPanel(player, game, back, new[]
                        {
                            "Le multiplicateur monte à chaque palier :",
                            string.Join(" > ", MultiplierSteps.Select(m => "x" + m.ToString("0.0#", CultureInfo.InvariantCulture))),
                            "Continuez pour monter, encaissez avant de tout perdre !",
                        },
                        ("Lancer", bet => PlayMultiplier(player, bet)));
                    break;
                case GameId.Ticket:
                    OpenTicket(player, back);
                    break;
                case GameId.Coffre:
                    OpenChests(player, back);
                    break;
            }
        }

        /// <summary>Panel de mise commun aux jeux : saisie de la mise puis un bouton par choix.</summary>
        private async void OpenBetPanel(Player player, GameId game, bool back, string[] rules, params (string label, Func<int, Task<bool>> play)[] choices)
        {
            CasinoClient client = await GetClient(player);
            GameSettings s = Game(game);
            Action reopen = () => OpenBetPanel(player, game, back, rules, choices);

            Panel panel = NewPanel(player, GameNames[game], UIPanel.PanelType.Input, reopen);
            panel.TextLines.Add($"{Color("Solde :", Colors.Info)} {Tokens(client.Tokens)}");
            panel.TextLines.Add($"{Color("Mise :", Colors.Info)} {MinBet(s)} à {MaxBet(s)} jetons");
            panel.TextLines.AddRange(rules);
            panel.SetInputPlaceholder($"Votre mise ({MinBet(s)} - {MaxBet(s)})");

            foreach ((string label, Func<int, Task<bool>> play) in choices)
            {
                panel.NextButton(label, async () =>
                {
                    if (!ReadAmount(player, panel.inputText, MinBet(s), MaxBet(s), out int bet) || !await play(bet))
                        reopen();
                });
            }
            if (back) panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        /// <summary>Débite des jetons pour une partie (contrôle activation, bornes et solde).</summary>
        private async Task<CasinoClient> TakeStake(Player player, GameId game, int stake, bool checkBounds)
        {
            GameSettings s = Game(game);
            if (!s.Enabled)
            {
                Notify(player, $"{GameNames[game]} est fermé pour le moment.", NotificationManager.Type.Warning);
                return null;
            }
            if (checkBounds && (stake < MinBet(s) || stake > MaxBet(s)))
            {
                Notify(player, $"La mise doit être comprise entre {MinBet(s)} et {MaxBet(s)} jetons.", NotificationManager.Type.Error);
                return null;
            }
            if (stake <= 0 || stake > HardMaxBet)
            {
                Notify(player, $"Mise maximale : {HardMaxBet} jetons.", NotificationManager.Type.Error);
                return null;
            }
            CasinoClient client = await GetClient(player);
            if (client.Tokens < stake)
            {
                Notify(player, $"Solde insuffisant : vous avez {Tokens(client.Tokens)}. Passez à la boutique !", NotificationManager.Type.Error);
                return null;
            }
            client.Tokens -= stake;
            client.WeeklyBet += stake;
            client.TotalBet += stake;
            await client.Save();
            return client;
        }

        /// <summary>Crédite le gain (0 si perdu) et notifie le joueur.</summary>
        private async Task Settle(Player player, CasinoClient client, GameId game, int stake, int payout, string detail)
        {
            if (payout > 0)
            {
                client.Tokens += payout;
                client.WeeklyWon += payout;
                client.TotalWon += payout;
                await client.Save();
            }
            int net = payout - stake;
            if (net > 0)
                Notify(player, $"Gagné ! +{Tokens(net)} ({detail})", NotificationManager.Type.Success);
            else if (net == 0)
                Notify(player, $"Égalité, mise rendue ({detail})", NotificationManager.Type.Info);
            else
                Notify(player, $"Perdu : -{Tokens(-net)} ({detail})", NotificationManager.Type.Error);

            Log(net > 0 ? "Gain" : net == 0 ? "Égalité" : "Perte", $"{player.FullName} - {GameNames[game]}",
                ("Mise", Tokens(stake)), ("Gain", Tokens(payout)), ("Résultat", detail), ("Solde", Tokens(client.Tokens)));
        }

        private void ShowResult(Player player, GameId game, CasinoClient client, int stake, int payout, params string[] lines)
        {
            Panel panel = NewPanel(player, $"{GameNames[game]} - Résultat", UIPanel.PanelType.Text,
                () => ShowResult(player, game, client, stake, payout, lines));
            panel.TextLines.AddRange(lines);
            panel.TextLines.Add("");
            int net = payout - stake;
            if (net > 0) panel.TextLines.Add(Bold(Color($"GAGNÉ : +{Tokens(net)}", Colors.Success)));
            else if (net == 0) panel.TextLines.Add(Bold(Color("ÉGALITÉ : mise rendue", Colors.Info)));
            else panel.TextLines.Add(Bold(Color($"PERDU : -{Tokens(-net)}", Colors.Error)));
            panel.TextLines.Add($"{Color("Nouveau solde :", Colors.Info)} {Tokens(client.Tokens)}");
            panel.PreviousButton("Rejouer");
            panel.CloseButton();
            Show(panel);
        }

        // ------------------------------------------------------------------
        //  Pile ou Face
        // ------------------------------------------------------------------

        private async Task<bool> PlayCoinFlip(Player player, int bet, bool heads)
        {
            CasinoClient client = await TakeStake(player, GameId.PileOuFace, bet, true);
            if (client == null) return false;

            bool resultHeads = _rng.Next(2) == 0;
            int payout = resultHeads == heads ? bet * 2 : 0;
            string result = resultHeads ? "Pile" : "Face";
            await Settle(player, client, GameId.PileOuFace, bet, payout, $"{result}");
            ShowResult(player, GameId.PileOuFace, client, bet, payout,
                $"Votre choix : {Bold(heads ? "Pile" : "Face")}", $"La pièce tombe sur : {Bold(result)}");
            return true;
        }

        // ------------------------------------------------------------------
        //  Roulette couleurs
        // ------------------------------------------------------------------

        private enum RouletteColor { Rouge, Noir, Vert }

        private static string RouletteText(RouletteColor color)
        {
            switch (color)
            {
                case RouletteColor.Rouge: return Bold(Color("ROUGE", Colors.Error));
                case RouletteColor.Vert: return Bold(Color("VERT", Colors.Success));
                default: return Bold("NOIR");
            }
        }

        private async Task<bool> PlayRoulette(Player player, int bet, RouletteColor choice)
        {
            CasinoClient client = await TakeStake(player, GameId.Roulette, bet, true);
            if (client == null) return false;

            double roll = _rng.NextDouble() * 100.0;
            RouletteColor result = roll < 47.5 ? RouletteColor.Rouge : roll < 95.0 ? RouletteColor.Noir : RouletteColor.Vert;
            int payout = result != choice ? 0 : result == RouletteColor.Vert ? bet * 10 : bet * 2;

            await Settle(player, client, GameId.Roulette, bet, payout, result.ToString());
            ShowResult(player, GameId.Roulette, client, bet, payout,
                $"Votre mise : {Tokens(bet)} sur {RouletteText(choice)}", $"La bille s'arrête sur : {RouletteText(result)}");
            return true;
        }

        // ------------------------------------------------------------------
        //  Machine à sous
        // ------------------------------------------------------------------

        private static readonly (string name, Colors color, int weight, int triple)[] SlotSymbols =
        {
            ("Cerise", Colors.Error, 30, 3),
            ("Citron", Colors.Warning, 25, 4),
            ("Orange", Colors.Orange, 20, 5),
            ("Cloche", Colors.Info, 12, 10),
            ("Étoile", Colors.Purple, 8, 20),
            ("7", Colors.Error, 5, 50),
        };

        private int SpinReel()
        {
            int total = SlotSymbols.Sum(s => s.weight);
            int roll = _rng.Next(total);
            for (int i = 0; i < SlotSymbols.Length; i++)
            {
                if (roll < SlotSymbols[i].weight) return i;
                roll -= SlotSymbols[i].weight;
            }
            return 0;
        }

        private async Task<bool> PlaySlots(Player player, int bet)
        {
            CasinoClient client = await TakeStake(player, GameId.MachineASous, bet, true);
            if (client == null) return false;

            int[] reels = { SpinReel(), SpinReel(), SpinReel() };
            int payout;
            string combo;
            if (reels[0] == reels[1] && reels[1] == reels[2])
            {
                payout = bet * SlotSymbols[reels[0]].triple;
                combo = $"Triple {SlotSymbols[reels[0]].name} (x{SlotSymbols[reels[0]].triple})";
            }
            else if (reels[0] == reels[1] || reels[1] == reels[2] || reels[0] == reels[2])
            {
                payout = (int)Math.Floor(bet * 1.5);
                combo = "Paire (x1,5)";
            }
            else
            {
                payout = 0;
                combo = "Aucune combinaison";
            }

            string line = "[ " + string.Join(" | ", reels.Select(r => Bold(Color(SlotSymbols[r].name, SlotSymbols[r].color)))) + " ]";
            await Settle(player, client, GameId.MachineASous, bet, payout, combo);
            ShowResult(player, GameId.MachineASous, client, bet, payout, Size(line, 30), combo);
            return true;
        }

        // ------------------------------------------------------------------
        //  Blackjack
        // ------------------------------------------------------------------

        private class BlackjackGame
        {
            public int Bet;
            public CasinoClient Client;
            public readonly List<int> PlayerCards = new List<int>();
            public readonly List<int> DealerCards = new List<int>();
            public bool Finished;
            public int Payout;
            public string Outcome;
        }

        private int DrawCard() => _rng.Next(1, 14);

        private static string CardName(int card)
        {
            switch (card)
            {
                case 1: return "A";
                case 11: return "V";
                case 12: return "D";
                case 13: return "R";
                default: return card.ToString();
            }
        }

        private static int Score(List<int> cards)
        {
            int total = cards.Sum(c => Math.Min(c, 10));
            if (cards.Contains(1) && total + 10 <= 21) total += 10;
            return total;
        }

        private static string Hand(List<int> cards) => string.Join(" ", cards.Select(CardName));

        private async Task<bool> PlayBlackjack(Player player, int bet)
        {
            CasinoClient client = await TakeStake(player, GameId.Blackjack, bet, true);
            if (client == null) return false;

            BlackjackGame game = new BlackjackGame { Bet = bet, Client = client };
            game.PlayerCards.Add(DrawCard());
            game.DealerCards.Add(DrawCard());
            game.PlayerCards.Add(DrawCard());
            game.DealerCards.Add(DrawCard());

            if (Score(game.PlayerCards) == 21)
                await BlackjackStand(player, game, false);
            else
                BlackjackPanel(player, game);
            return true;
        }

        private void BlackjackPanel(Player player, BlackjackGame game)
        {
            Panel panel = NewPanel(player, "Blackjack - Partie", UIPanel.PanelType.Text, () => BlackjackPanel(player, game));
            panel.TextLines.Add($"{Color("Mise :", Colors.Info)} {Tokens(game.Bet)}");
            panel.TextLines.Add($"{Color("Vos cartes :", Colors.Info)} {Bold(Hand(game.PlayerCards))} ({Score(game.PlayerCards)})");
            if (game.Finished)
                panel.TextLines.Add($"{Color("Banque :", Colors.Info)} {Bold(Hand(game.DealerCards))} ({Score(game.DealerCards)})");
            else
                panel.TextLines.Add($"{Color("Banque :", Colors.Info)} {Bold(CardName(game.DealerCards[0]))} ?");

            if (game.Finished)
            {
                panel.TextLines.Add("");
                panel.TextLines.Add(Bold(game.Outcome));
                panel.TextLines.Add($"{Color("Nouveau solde :", Colors.Info)} {Tokens(game.Client.Tokens)}");
                panel.PreviousButton("Rejouer");
                panel.CloseButton();
            }
            else
            {
                panel.NextButton("Tirer", async () => await BlackjackHit(player, game));
                panel.NextButton("Rester", async () => await BlackjackStand(player, game, true));
            }
            Show(panel);
        }

        private async Task BlackjackHit(Player player, BlackjackGame game)
        {
            if (game.Finished) return;
            game.PlayerCards.Add(DrawCard());
            int score = Score(game.PlayerCards);
            if (score > 21)
            {
                await BlackjackFinish(player, game, 0, Color($"Vous dépassez 21 ({score}) : perdu.", Colors.Error));
                return;
            }
            if (score == 21)
            {
                await BlackjackStand(player, game, true);
                return;
            }
            BlackjackPanel(player, game);
        }

        private async Task BlackjackStand(Player player, BlackjackGame game, bool dealerPlays)
        {
            if (game.Finished) return;
            int playerScore = Score(game.PlayerCards);
            // La banque tire jusqu'à 17 (sauf blackjack naturel du joueur : on compare directement)
            while (dealerPlays && Score(game.DealerCards) < 17)
                game.DealerCards.Add(DrawCard());
            int dealerScore = Score(game.DealerCards);

            if (dealerScore > 21 || playerScore > dealerScore)
                await BlackjackFinish(player, game, game.Bet * 2, Color($"Vous gagnez ({playerScore} contre {dealerScore}) !", Colors.Success));
            else if (playerScore == dealerScore)
                await BlackjackFinish(player, game, game.Bet, Color($"Égalité ({playerScore}) : mise rendue.", Colors.Info));
            else
                await BlackjackFinish(player, game, 0, Color($"La banque gagne ({dealerScore} contre {playerScore}).", Colors.Error));
        }

        private async Task BlackjackFinish(Player player, BlackjackGame game, int payout, string outcome)
        {
            if (game.Finished) return;
            game.Finished = true;
            game.Payout = payout;
            game.Outcome = outcome;
            await Settle(player, game.Client, GameId.Blackjack, game.Bet, payout,
                $"{Score(game.PlayerCards)} contre {Score(game.DealerCards)}");
            BlackjackPanel(player, game);
        }

        // ------------------------------------------------------------------
        //  Dé - Double ou Rien
        // ------------------------------------------------------------------

        private async Task<bool> PlayDice(Player player, int bet)
        {
            CasinoClient client = await TakeStake(player, GameId.De, bet, true);
            if (client == null) return false;

            int roll = _rng.Next(1, 7);
            int payout = roll >= 5 ? bet * 2 : 0;
            await Settle(player, client, GameId.De, bet, payout, $"dé : {roll}");
            ShowResult(player, GameId.De, client, bet, payout, $"Le dé roule... {Size(Bold(roll.ToString()), 30)}", "Il fallait un 5 ou un 6.");
            return true;
        }

        // ------------------------------------------------------------------
        //  Machine des Multiplicateurs
        // ------------------------------------------------------------------

        private static readonly double[] MultiplierSteps = { 1.2, 1.5, 2.0, 3.0, 5.0, 10.0 };
        private static readonly int[] MultiplierChances = { 80, 76, 71, 63, 57, 48 };

        private class MultiplierGame
        {
            public int Bet;
            public CasinoClient Client;
            public int Step = -1;
            public bool Finished;
            public bool Lost;
        }

        private static int MultiplierPayout(MultiplierGame game) =>
            game.Step < 0 ? game.Bet : (int)Math.Floor(game.Bet * MultiplierSteps[game.Step]);

        private async Task<bool> PlayMultiplier(Player player, int bet)
        {
            CasinoClient client = await TakeStake(player, GameId.Multiplicateur, bet, true);
            if (client == null) return false;

            await MultiplierNext(player, new MultiplierGame { Bet = bet, Client = client });
            return true;
        }

        private async Task MultiplierNext(Player player, MultiplierGame game)
        {
            if (game.Finished) return;
            int next = game.Step + 1;
            if (_rng.Next(100) < MultiplierChances[next])
            {
                game.Step = next;
                if (game.Step == MultiplierSteps.Length - 1)
                {
                    await MultiplierCashOut(player, game);
                    return;
                }
            }
            else
            {
                game.Finished = true;
                game.Lost = true;
                await Settle(player, game.Client, GameId.Multiplicateur, game.Bet, 0, $"perdu au palier x{MultiplierSteps[next]:0.0#}");
            }
            MultiplierPanel(player, game);
        }

        private async Task MultiplierCashOut(Player player, MultiplierGame game)
        {
            if (game.Finished) return;
            game.Finished = true;
            await Settle(player, game.Client, GameId.Multiplicateur, game.Bet, MultiplierPayout(game),
                $"encaissé à x{MultiplierSteps[game.Step]:0.0#}");
            MultiplierPanel(player, game);
        }

        private void MultiplierPanel(Player player, MultiplierGame game)
        {
            Panel panel = NewPanel(player, "Multiplicateurs - Partie", UIPanel.PanelType.Text, () => MultiplierPanel(player, game));
            panel.TextLines.Add($"{Color("Mise :", Colors.Info)} {Tokens(game.Bet)}");

            List<string> ladder = new List<string>();
            for (int i = 0; i < MultiplierSteps.Length; i++)
            {
                string step = "x" + MultiplierSteps[i].ToString("0.0#", CultureInfo.InvariantCulture);
                ladder.Add(i == game.Step ? Bold(Color($"[{step}]", Colors.Success)) : i < game.Step ? Color(step, Colors.Grey) : step);
            }
            panel.TextLines.Add(string.Join(" > ", ladder));

            if (game.Finished)
            {
                panel.TextLines.Add("");
                panel.TextLines.Add(game.Lost
                    ? Bold(Color("Le multiplicateur a explosé : mise perdue !", Colors.Error))
                    : Bold(Color($"Encaissé : {Tokens(MultiplierPayout(game))}", Colors.Success)));
                panel.TextLines.Add($"{Color("Nouveau solde :", Colors.Info)} {Tokens(game.Client.Tokens)}");
                panel.PreviousButton("Rejouer");
                panel.CloseButton();
            }
            else
            {
                int next = game.Step + 1;
                panel.TextLines.Add($"{Color("Gain actuel :", Colors.Info)} {Tokens(MultiplierPayout(game))}");
                panel.TextLines.Add($"Palier suivant : x{MultiplierSteps[next]:0.0#} - chance de réussite {MultiplierChances[next]} %");
                panel.NextButton("Continuer", async () => await MultiplierNext(player, game));
                panel.NextButton("Encaisser", async () => await MultiplierCashOut(player, game));
            }
            Show(panel);
        }

        // ------------------------------------------------------------------
        //  Ticket à gratter
        // ------------------------------------------------------------------

        private static readonly int[] TicketMultipliers = { 0, 0, 0, 0, 0, 1, 2, 4 };

        private async void OpenTicket(Player player, bool back)
        {
            CasinoClient client = await GetClient(player);
            int price = Price(Game(GameId.Ticket));
            Panel panel = NewPanel(player, GameNames[GameId.Ticket], UIPanel.PanelType.Tab, () => OpenTicket(player, back));

            Info(panel, $"{Color("Solde :", Colors.Info)} {Tokens(client.Tokens)} - {Color("Prix :", Colors.Info)} {Tokens(price)}");
            Info(panel, Color($"Grattez 1 case sur 8 : gains de 0 à {Tokens(price * TicketMultipliers.Max())}", Colors.Grey));
            for (int i = 0; i < TicketMultipliers.Length; i++)
            {
                int chosen = i;
                panel.AddTabLine($"Case {i + 1}", async _ =>
                {
                    CasinoClient payer = await TakeStake(player, GameId.Ticket, price, false);
                    if (payer == null) return;
                    Leave(panel);

                    int[] grid = TicketMultipliers.OrderBy(_ => _rng.Next()).ToArray();
                    int payout = price * grid[chosen];
                    await Settle(player, payer, GameId.Ticket, price, payout, $"case {chosen + 1} : {Tokens(payout)}");

                    List<string> lines = new List<string> { $"Vous grattez la case {chosen + 1}...", "" };
                    for (int c = 0; c < grid.Length; c++)
                    {
                        string value = $"Case {c + 1} : {Tokens(price * grid[c])}";
                        lines.Add(c == chosen ? Bold(Color("> " + value, Colors.Warning)) : Color(value, Colors.Grey));
                    }
                    ShowResult(player, GameId.Ticket, payer, price, payout, lines.ToArray());
                });
            }
            panel.AddButton("Gratter", _ => panel.SelectTab());
            if (back) panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ------------------------------------------------------------------
        //  Coffre Mystère
        // ------------------------------------------------------------------

        private async void OpenChests(Player player, bool back)
        {
            CasinoClient client = await GetClient(player);
            GameSettings s = Game(GameId.Coffre);
            int price = Price(s);
            int prize = Math.Max(0, s.Prize);
            Panel panel = NewPanel(player, GameNames[GameId.Coffre], UIPanel.PanelType.Tab, () => OpenChests(player, back));

            Info(panel, $"{Color("Solde :", Colors.Info)} {Tokens(client.Tokens)} - {Color("Prix :", Colors.Info)} {Tokens(price)}");
            Info(panel, Color($"1 coffre gagnant sur 3 : +{Tokens(prize)}", Colors.Grey));
            for (int i = 0; i < 3; i++)
            {
                int chosen = i;
                panel.AddTabLine($"Coffre {i + 1}", async _ =>
                {
                    CasinoClient payer = await TakeStake(player, GameId.Coffre, price, false);
                    if (payer == null) return;
                    Leave(panel);

                    int winner = _rng.Next(3);
                    int payout = winner == chosen ? prize : 0;
                    await Settle(player, payer, GameId.Coffre, price, payout, $"coffre {chosen + 1}, gagnant : {winner + 1}");
                    ShowResult(player, GameId.Coffre, payer, price, payout,
                        $"Vous ouvrez le coffre {chosen + 1}...",
                        winner == chosen ? Color($"Il contient {Tokens(prize)} !", Colors.Success) : Color("Il est vide.", Colors.Error),
                        $"Le coffre gagnant était le n°{winner + 1}.");
                });
            }
            panel.AddButton("Ouvrir", _ => panel.SelectTab());
            if (back) panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ==================================================================
        //  Boutique
        // ==================================================================

        private double ResaleRate => Math.Round(Cfg.TokenPrice * Cfg.ResalePercent / 100.0, 4);

        private async void OpenShop(Player player, bool back)
        {
            CasinoClient client = await GetClient(player);
            Panel panel = NewPanel(player, "Boutique", UIPanel.PanelType.Tab, () => OpenShop(player, back));

            Info(panel, $"{Color("Solde :", Colors.Info)} {Tokens(client.Tokens)}");
            Link(panel, $"Acheter des jetons {Color($"(1 jeton = {Euros(Cfg.TokenPrice)})", Colors.Grey)}", () => BuyTokens(player));
            if (Cfg.ShopResale)
                Link(panel, $"Revendre des jetons {Color($"(1 jeton = {Euros(ResaleRate)})", Colors.Grey)}", () => SellTokens(player));

            SelectButton(panel);
            if (back) panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private async void BuyTokens(Player player)
        {
            CasinoClient client = await GetClient(player);
            Panel panel = NewPanel(player, "Achat de jetons", UIPanel.PanelType.Input, () => BuyTokens(player));
            panel.TextLines.Add($"{Color("Solde :", Colors.Info)} {Tokens(client.Tokens)}");
            panel.TextLines.Add($"{Color("Argent liquide :", Colors.Info)} {Euros(player.Money)}");
            panel.TextLines.Add($"{Color("Prix :", Colors.Info)} 1 jeton = {Euros(Cfg.TokenPrice)}");
            panel.SetInputPlaceholder("Nombre de jetons à acheter");

            panel.PreviousButtonWithAction("Acheter", async () =>
            {
                if (!ReadAmount(player, panel.inputText, 1, 1000000, out int amount)) return false;
                double cost = Math.Round(amount * Cfg.TokenPrice, 2);
                if (player.Money < cost)
                {
                    Notify(player, $"Il vous faut {Euros(cost)} en liquide.", NotificationManager.Type.Error);
                    return false;
                }
                player.AddMoney(-cost, "Loris Casino - achat de jetons");
                CasinoBiz()?.AddBankMoney(cost, $"Loris Casino - achat de jetons par {player.FullName}");
                client.Tokens += amount;
                await client.Save();
                Notify(player, $"Achat de {Tokens(amount)} pour {Euros(cost)}.", NotificationManager.Type.Success);
                Log("Achat de jetons", $"{player.FullName} a acheté {Tokens(amount)}.", ("Prix", Euros(cost)), ("Solde", Tokens(client.Tokens)));
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private async void SellTokens(Player player)
        {
            CasinoClient client = await GetClient(player);
            Panel panel = NewPanel(player, "Revente de jetons", UIPanel.PanelType.Input, () => SellTokens(player));
            panel.TextLines.Add($"{Color("Solde :", Colors.Info)} {Tokens(client.Tokens)}");
            panel.TextLines.Add($"{Color("Reprise :", Colors.Info)} 1 jeton = {Euros(ResaleRate)} ({Cfg.ResalePercent} %)");
            panel.SetInputPlaceholder("Nombre de jetons à revendre");

            panel.PreviousButtonWithAction("Revendre", async () =>
            {
                if (!Cfg.ShopResale) return true;
                if (!ReadAmount(player, panel.inputText, 1, client.Tokens, out int amount)) return false;
                double euros = Math.Round(amount * ResaleRate, 2);
                if (!PayOut(player, euros, "Loris Casino - revente de jetons")) return false;
                client.Tokens -= amount;
                await client.Save();
                Notify(player, $"Revente de {Tokens(amount)} pour {Euros(euros)}.", NotificationManager.Type.Success);
                Log("Revente de jetons", $"{player.FullName} a revendu {Tokens(amount)}.", ("Montant", Euros(euros)), ("Solde", Tokens(client.Tokens)));
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ==================================================================
        //  Espace employé
        // ==================================================================

        private void OpenEmployeeSpace(Player player, bool back)
        {
            if (!CanUseEmployeeSpace(player))
            {
                Notify(player, "Espace réservé aux employés du casino.", NotificationManager.Type.Error);
                return;
            }
            Panel panel = NewPanel(player, "Espace employé", UIPanel.PanelType.Tab, () => OpenEmployeeSpace(player, back));

            Link(panel, "Convertir les jetons d'un joueur proche (jetons > €)", () => ConvertNearby(player));
            if (IsCasinoStaff(player))
                Link(panel, "Gérer les jetons d'un joueur", () => TokenPlayersList(player));
            if (CanManageStaff(player))
                Link(panel, "Gérer le staff du casino", () => StaffPanel(player));
            if (IsCasinoAdmin(player))
                Link(panel, Color("Configuration", Colors.Warning), () => OpenConfig(player, true));

            SelectButton(panel);
            if (back) panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private async void ConvertNearby(Player player)
        {
            Player target = player.GetClosestPlayer();
            if (target == null || target == player)
            {
                Notify(player, "Aucun joueur à proximité.", NotificationManager.Type.Warning);
                return;
            }
            CasinoClient targetClient = await GetClient(target);

            Panel panel = NewPanel(player, "Conversion jetons > €", UIPanel.PanelType.Input, () => ConvertNearby(player));
            panel.TextLines.Add($"{Color("Joueur :", Colors.Info)} {target.FullName}");
            panel.TextLines.Add($"{Color("Jetons :", Colors.Info)} {Tokens(targetClient.Tokens)}");
            panel.TextLines.Add($"{Color("Taux :", Colors.Info)} 1 jeton = {Euros(ResaleRate)}");
            panel.TextLines.Add("Le joueur devra confirmer la conversion.");
            panel.SetInputPlaceholder("Nombre de jetons à convertir");

            panel.PreviousButtonWithAction("Proposer", () =>
            {
                if (!ReadAmount(player, panel.inputText, 1, targetClient.Tokens, out int amount)) return Task.FromResult(false);
                double euros = Math.Round(amount * ResaleRate, 2);
                ConfirmConversion(player, target, amount, euros);
                Notify(player, $"Proposition envoyée à {target.FullName}.", NotificationManager.Type.Info);
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private void ConfirmConversion(Player employee, Player target, int amount, double euros)
        {
            Panel panel = NewPanel(target, "Conversion de jetons", UIPanel.PanelType.Text,
                () => ConfirmConversion(employee, target, amount, euros));
            panel.TextLines.Add($"{employee.FullName} vous propose de convertir");
            panel.TextLines.Add(Bold($"{Tokens(amount)} contre {Euros(euros)}"));
            panel.TextLines.Add("Acceptez-vous ?");

            panel.CloseButtonWithAction("Accepter", async () =>
            {
                CasinoClient client = await GetClient(target);
                if (client.Tokens < amount)
                {
                    Notify(target, "Vous n'avez plus assez de jetons.", NotificationManager.Type.Error);
                    Notify(employee, $"{target.FullName} n'a plus assez de jetons.", NotificationManager.Type.Error);
                    return true;
                }
                if (!PayOut(target, euros, "Loris Casino - conversion de jetons"))
                {
                    Notify(employee, "La conversion a échoué (fonds du casino ou argent liquide).", NotificationManager.Type.Error);
                    return true;
                }
                client.Tokens -= amount;
                await client.Save();
                Notify(target, $"Vous avez reçu {Euros(euros)} contre {Tokens(amount)}.", NotificationManager.Type.Success);
                Notify(employee, $"Conversion effectuée pour {target.FullName}.", NotificationManager.Type.Success);
                Log("Conversion jetons > €", $"{employee.FullName} a converti {Tokens(amount)} de {target.FullName}.",
                    ("Montant", Euros(euros)), ("Solde joueur", Tokens(client.Tokens)));
                return true;
            });
            panel.CloseButtonWithAction("Refuser", () =>
            {
                Notify(employee, $"{target.FullName} a refusé la conversion.", NotificationManager.Type.Warning);
                return Task.FromResult(true);
            });
            Show(panel);
        }

        private void TokenPlayersList(Player staff)
        {
            if (!IsCasinoStaff(staff)) return;
            Panel panel = NewPanel(staff, "Jetons des joueurs", UIPanel.PanelType.Tab, () => TokenPlayersList(staff));

            Link(panel, Color("Rechercher par ID de personnage", Colors.Info), () => SearchClientById(staff));
            foreach (Player p in Nova.server.GetAllInGamePlayers().OrderBy(p => p.FullName))
            {
                int characterId = p.character.Id;
                string tokens = _clients.TryGetValue(characterId, out CasinoClient c) ? Tokens(c.Tokens) : "?";
                Link(panel, $"{p.FullName} {Color($"[ID {characterId}] - {tokens}", Colors.Grey)}", () => ManageTokens(staff, characterId));
            }

            SelectButton(panel);
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private void SearchClientById(Player staff)
        {
            Panel panel = NewPanel(staff, "Rechercher un joueur", UIPanel.PanelType.Input, () => SearchClientById(staff));
            panel.TextLines.Add("ID du personnage (visible dans la liste des joueurs).");
            panel.SetInputPlaceholder("ID du personnage");
            panel.NextButton("Rechercher", () =>
            {
                if (!ReadAmount(staff, panel.inputText, 1, int.MaxValue, out int characterId))
                {
                    SearchClientById(staff);
                    return;
                }
                ManageTokens(staff, characterId);
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private async void ManageTokens(Player staff, int characterId)
        {
            if (!IsCasinoStaff(staff)) return;
            CasinoClient client = await FindClient(characterId);
            if (client == null)
            {
                Notify(staff, $"Aucun compte casino pour l'ID {characterId}.", NotificationManager.Type.Error);
                return;
            }

            Panel panel = NewPanel(staff, "Gestion des jetons", UIPanel.PanelType.Input, () => ManageTokens(staff, characterId));
            panel.TextLines.Add($"{Color("Joueur :", Colors.Info)} {client.Name} (ID {client.CharacterId})");
            panel.TextLines.Add($"{Color("Solde :", Colors.Info)} {Tokens(client.Tokens)}");
            panel.TextLines.Add($"{Color("Cette semaine :", Colors.Info)} misé {client.WeeklyBet} / gagné {client.WeeklyWon}");
            panel.SetInputPlaceholder("Nombre de jetons");

            panel.AddButton("Ajouter", async _ =>
            {
                if (!ReadAmount(staff, panel.inputText, 1, 1000000, out int amount)) return;
                client.Tokens += amount;
                await client.Save();
                TokensChanged(staff, client, $"+{Tokens(amount)}");
                panel.Refresh();
            });
            panel.AddButton("Retirer", async _ =>
            {
                if (!ReadAmount(staff, panel.inputText, 1, client.Tokens, out int amount)) return;
                client.Tokens -= amount;
                await client.Save();
                TokensChanged(staff, client, $"-{Tokens(amount)}");
                panel.Refresh();
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private void TokensChanged(Player staff, CasinoClient client, string change)
        {
            Notify(staff, $"{client.Name} : {change} (solde {Tokens(client.Tokens)}).", NotificationManager.Type.Success);
            Player target = Nova.server.GetAllInGamePlayers().FirstOrDefault(p => p.character.Id == client.CharacterId);
            if (target != null && target != staff)
                Notify(target, $"Le casino a modifié votre solde : {change} (solde {Tokens(client.Tokens)}).", NotificationManager.Type.Info);
            LogStaff(staff, $"Jetons de {client.Name} (ID {client.CharacterId}) : {change}, nouveau solde {Tokens(client.Tokens)}");
        }

        private async void StaffPanel(Player player)
        {
            if (!CanManageStaff(player)) return;
            List<CasinoStaff> staffList = await CasinoStaff.QueryAll();
            Panel panel = NewPanel(player, "Staff du casino", UIPanel.PanelType.Tab, () => StaffPanel(player));

            if (staffList.Count == 0) Info(panel, "Aucun membre du staff enregistré.");
            foreach (CasinoStaff staff in staffList)
            {
                panel.AddTabLine($"{staff.Name} {Color($"[ID {staff.CharacterId}] ajouté par {staff.AddedBy}", Colors.Grey)}", async _ =>
                {
                    if (await staff.Delete())
                    {
                        _staffIds.Remove(staff.CharacterId);
                        Notify(player, $"{staff.Name} retiré du staff.", NotificationManager.Type.Success);
                        LogStaff(player, $"{staff.Name} (ID {staff.CharacterId}) retiré du staff casino");
                    }
                    panel.Refresh();
                });
            }

            panel.NextButton("Ajouter", () => StaffAddPanel(player));
            if (staffList.Count > 0) panel.AddButton(Color("Retirer", Colors.Error), _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private void StaffAddPanel(Player player)
        {
            if (!CanManageStaff(player)) return;
            Panel panel = NewPanel(player, "Ajouter au staff", UIPanel.PanelType.Tab, () => StaffAddPanel(player));

            List<Player> candidates = Nova.server.GetAllInGamePlayers()
                .Where(p => !_staffIds.Contains(p.character.Id)).OrderBy(p => p.FullName).ToList();
            if (candidates.Count == 0) Info(panel, "Aucun joueur à ajouter.");
            foreach (Player p in candidates)
            {
                panel.AddTabLine($"{p.FullName} {Color($"[ID {p.character.Id}]", Colors.Grey)}", async _ =>
                {
                    if (_staffIds.Contains(p.character.Id)) return;
                    CasinoStaff staff = new CasinoStaff
                    {
                        CharacterId = p.character.Id,
                        Name = p.FullName,
                        AddedBy = player.FullName,
                        AddedAt = Now,
                    };
                    if (!await staff.Save())
                    {
                        Notify(player, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                        return;
                    }
                    _staffIds.Add(staff.CharacterId);
                    Notify(player, $"{p.FullName} ajouté au staff du casino.", NotificationManager.Type.Success);
                    Notify(p, "Vous faites maintenant partie du staff du casino.", NotificationManager.Type.Success);
                    LogStaff(player, $"{p.FullName} (ID {p.character.Id}) ajouté au staff casino");
                    panel.Previous();
                });
            }

            SelectButton(panel);
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ==================================================================
        //  Configuration (admin)
        // ==================================================================

        private bool CheckAdmin(Player player)
        {
            if (IsCasinoAdmin(player)) return true;
            Notify(player, $"Accès réservé aux admins en service (AdminLevel {Cfg.AdminLevel} minimum).", NotificationManager.Type.Error);
            return false;
        }

        private void OpenConfig(Player player, bool back)
        {
            if (!CheckAdmin(player)) return;
            if (!_configLoaded)
            {
                Notify(player, "Configuration en cours de chargement, réessayez dans un instant.", NotificationManager.Type.Warning);
                return;
            }
            Panel panel = NewPanel(player, "Configuration", UIPanel.PanelType.Tab, () => OpenConfig(player, back));
            Bizs biz = CasinoBiz();

            Link(panel, $"Titre des panels : {Bold(Cfg.PanelTitle)}", () =>
                EditValue(player, "Titre des panels", Cfg.PanelTitle, true, v =>
                {
                    if (v.Length < 2 || v.Length > 40) return "Le titre doit faire entre 2 et 40 caractères.";
                    Cfg.PanelTitle = v;
                    return null;
                }));
            Link(panel, $"AdminLevel requis : {Bold(Cfg.AdminLevel.ToString())}", () =>
                EditInt(player, "AdminLevel requis", Cfg.AdminLevel, 0, 100, v => Cfg.AdminLevel = v));
            Link(panel, $"Entreprise casino : {Bold(biz != null ? $"[{biz.Id}] {biz.BizName}" : "aucune")}", () => PickCasinoBiz(player));
            Link(panel, $"Valeur du jeton : {Bold(Euros(Cfg.TokenPrice))}", () =>
                EditValue(player, "Valeur du jeton (€)", Cfg.TokenPrice.ToString(CultureInfo.InvariantCulture), false, v =>
                {
                    if (!double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double price) || price < 0.01 || price > 100000)
                        return "Saisissez un prix entre 0.01 et 100000.";
                    Cfg.TokenPrice = Math.Round(price, 2);
                    return null;
                }));
            Link(panel, $"Revente des jetons : {Bold(Cfg.ResalePercent + " %")}", () =>
                EditInt(player, "Revente des jetons (%)", Cfg.ResalePercent, 1, 100, v => Cfg.ResalePercent = v));
            Link(panel, $"Récompense quotidienne : {Bold(Tokens(Cfg.DailyReward))}", () =>
                EditInt(player, "Récompense quotidienne (0 = désactivée)", Cfg.DailyReward, 0, 100000, v => Cfg.DailyReward = v));
            panel.AddTabLine($"Revente libre en boutique : {YesNo(Cfg.ShopResale)}", async _ =>
            {
                Cfg.ShopResale = !Cfg.ShopResale;
                await ConfigChanged(player, $"Revente en boutique : {(Cfg.ShopResale ? "oui" : "non")}");
                panel.Refresh();
            });
            panel.AddTabLine($"Menu casino partout (AAMenu > Interactions) : {YesNo(Cfg.MenuAnywhere)}", async _ =>
            {
                Cfg.MenuAnywhere = !Cfg.MenuAnywhere;
                await ConfigChanged(player, $"Menu casino partout : {(Cfg.MenuAnywhere ? "oui" : "non")}");
                panel.Refresh();
            });
            Link(panel, Color("Jeux (activation, mises)", Colors.Info), () => GamesConfig(player));
            Link(panel, Color("Points du casino", Colors.Info), () => PointsMenu(player));
            Link(panel, Color("Discord", Colors.Info), () => DiscordConfig(player));
            Link(panel, Color("Espace employé (jetons, staff)", Colors.Orange), () => OpenEmployeeSpace(player, true));
            Link(panel, Color("Remettre le classement à zéro", Colors.Error), () => ConfirmWeeklyReset(player));

            SelectButton(panel);
            if (back) panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private async Task ConfigChanged(Player player, string change)
        {
            if (await SaveConfig())
                Notify(player, $"Enregistré : {change}", NotificationManager.Type.Success);
            else
                Notify(player, "Erreur lors de l'enregistrement de la configuration.", NotificationManager.Type.Error);
            LogStaff(player, $"Configuration - {change}");
        }

        /// <summary>Saisie d'une valeur. apply renvoie un message d'erreur, ou null si la valeur est acceptée.</summary>
        private void EditValue(Player player, string label, string current, bool closeAfter, Func<string, string> apply)
        {
            if (!CheckAdmin(player)) return;
            Panel panel = NewPanel(player, $"Modifier : {label}", UIPanel.PanelType.Input,
                () => EditValue(player, label, current, closeAfter, apply));
            panel.TextLines.Add(Bold(label));
            panel.TextLines.Add($"{Color("Valeur actuelle :", Colors.Info)} {current}");
            panel.SetInputPlaceholder(current);

            Func<Task<bool>> save = async () =>
            {
                string value = panel.inputText?.Trim() ?? "";
                string error = apply(value);
                if (error != null)
                {
                    Notify(player, error, NotificationManager.Type.Error);
                    return false;
                }
                await ConfigChanged(player, $"{label} : {value}");
                return true;
            };
            // Le titre change le nom des panels : on ferme le menu au lieu de revenir en arrière.
            if (closeAfter) panel.CloseButtonWithAction("Enregistrer", save);
            else panel.PreviousButtonWithAction("Enregistrer", save);
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private void EditInt(Player player, string label, int current, int min, int max, Action<int> set)
        {
            EditValue(player, label, current.ToString(), false, v =>
            {
                if (!TryParseInt(v, min, max, out int value)) return $"Saisissez un nombre entier entre {min} et {max}.";
                set(value);
                return null;
            });
        }

        private void PickCasinoBiz(Player player)
        {
            if (!CheckAdmin(player)) return;
            Panel panel = NewPanel(player, "Entreprise casino", UIPanel.PanelType.Tab, () => PickCasinoBiz(player));

            panel.AddTabLine(Color("Aucune entreprise", Colors.Grey), async _ =>
            {
                Cfg.CasinoBizId = 0;
                await ConfigChanged(player, "Entreprise casino : aucune");
                panel.Previous();
            });
            foreach (Bizs biz in Nova.biz.bizs.OrderBy(b => b.Id))
            {
                int bizId = biz.Id;
                string name = biz.BizName;
                panel.AddTabLine($"[{bizId}] {name}" + (bizId == Cfg.CasinoBizId ? Color(" (actuelle)", Colors.Success) : ""), async _ =>
                {
                    Cfg.CasinoBizId = bizId;
                    await ConfigChanged(player, $"Entreprise casino : [{bizId}] {name}");
                    panel.Previous();
                });
            }

            SelectButton(panel);
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private void GamesConfig(Player player)
        {
            if (!CheckAdmin(player)) return;
            Panel panel = NewPanel(player, "Configuration des jeux", UIPanel.PanelType.Tab, () => GamesConfig(player));
            foreach (GameId game in GameNames.Keys)
            {
                GameSettings s = Game(game);
                string state = s.Enabled ? Color("activé", Colors.Success) : Color("désactivé", Colors.Error);
                Link(panel, $"{GameNames[game]} : {state} {Color($"({BetRange(game)})", Colors.Grey)}", () => GameConfig(player, game));
            }
            SelectButton(panel);
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private void GameConfig(Player player, GameId game)
        {
            if (!CheckAdmin(player)) return;
            GameSettings s = Game(game);
            string name = GameNames[game];
            Panel panel = NewPanel(player, $"Jeu : {name}", UIPanel.PanelType.Tab, () => GameConfig(player, game));

            panel.AddTabLine($"Activé : {YesNo(s.Enabled)}", async _ =>
            {
                s.Enabled = !s.Enabled;
                await ConfigChanged(player, $"{name} {(s.Enabled ? "activé" : "désactivé")}");
                panel.Refresh();
            });
            if (IsFixedPrice(game))
            {
                Link(panel, $"Prix : {Bold(Tokens(Price(s)))}", () =>
                    EditInt(player, $"{name} - prix (max {HardMaxBet})", Price(s), 1, HardMaxBet, v => s.Price = v));
                if (game == GameId.Coffre)
                    Link(panel, $"Gain du coffre gagnant : {Bold(Tokens(s.Prize))}", () =>
                        EditInt(player, $"{name} - gain", s.Prize, 1, 100000, v => s.Prize = v));
            }
            else
            {
                Link(panel, $"Mise minimum : {Bold(Tokens(MinBet(s)))}", () =>
                    EditInt(player, $"{name} - mise minimum", MinBet(s), 1, HardMaxBet, v =>
                    {
                        s.MinBet = v;
                        if (s.MaxBet < v) s.MaxBet = v;
                    }));
                Link(panel, $"Mise maximum : {Bold(Tokens(MaxBet(s)))} {Color($"(plafond {HardMaxBet})", Colors.Grey)}", () =>
                    EditInt(player, $"{name} - mise maximum (max {HardMaxBet})", MaxBet(s), 1, HardMaxBet, v =>
                    {
                        s.MaxBet = v;
                        if (s.MinBet > v) s.MinBet = v;
                    }));
            }

            SelectButton(panel);
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private static readonly Regex HexColor = new Regex("^#?[0-9A-Fa-f]{6}$");

        private void DiscordConfig(Player player)
        {
            if (!CheckAdmin(player)) return;
            Panel panel = NewPanel(player, "Discord", UIPanel.PanelType.Tab, () => DiscordConfig(player));

            panel.AddTabLine($"Webhook activé : {YesNo(Cfg.WebhookEnabled)}", async _ =>
            {
                Cfg.WebhookEnabled = !Cfg.WebhookEnabled;
                await ConfigChanged(player, $"Webhook Discord {(Cfg.WebhookEnabled ? "activé" : "désactivé")}");
                panel.Refresh();
            });
            string url = string.IsNullOrEmpty(Cfg.WebhookUrl) ? "non défini" : Truncate(Cfg.WebhookUrl, 40);
            Link(panel, $"URL du webhook : {Color(url, Colors.Grey)}", () =>
                EditValue(player, "URL du webhook Discord", Cfg.WebhookUrl ?? "", false, v =>
                {
                    if (!v.StartsWith("https://")) return "L'URL doit commencer par https://";
                    Cfg.WebhookUrl = v;
                    return null;
                }));
            Link(panel, $"Couleur des embeds : {Color(Cfg.WebhookColor, Cfg.WebhookColor.StartsWith("#") ? Cfg.WebhookColor : "#" + Cfg.WebhookColor)}", () =>
                EditValue(player, "Couleur hex des embeds (ex : #9B59B6)", Cfg.WebhookColor, false, v =>
                {
                    if (!HexColor.IsMatch(v)) return "Couleur invalide (format #RRGGBB).";
                    Cfg.WebhookColor = v.StartsWith("#") ? v.ToUpperInvariant() : "#" + v.ToUpperInvariant();
                    return null;
                }));
            panel.AddTabLine("Envoyer un message de test", _ =>
            {
                if (!Cfg.WebhookEnabled || string.IsNullOrWhiteSpace(Cfg.WebhookUrl))
                {
                    Notify(player, "Activez le webhook et définissez l'URL d'abord.", NotificationManager.Type.Warning);
                    return;
                }
                Log("Test", $"Webhook configuré par {player.FullName}. {Credit}");
                Notify(player, "Message de test envoyé.", NotificationManager.Type.Success);
            });

            SelectButton(panel);
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private void ConfirmWeeklyReset(Player player)
        {
            if (!CheckAdmin(player)) return;
            Panel panel = NewPanel(player, "Remise à zéro", UIPanel.PanelType.Text, () => ConfirmWeeklyReset(player));
            panel.TextLines.Add("Remettre à zéro les mises et gains de la semaine de tous les joueurs ?");
            panel.TextLines.Add(Color("Les jetons des joueurs ne sont pas touchés.", Colors.Grey));
            panel.PreviousButtonWithAction(Color("Confirmer", Colors.Error), async () =>
            {
                await WeeklyReset(player.FullName);
                Notify(player, "Classement de la semaine remis à zéro.", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        // ==================================================================
        //  Maintenance : reset hebdomadaire (chaque lundi 00:00)
        // ==================================================================

        private DateTime NextWeeklyReset()
        {
            DateTime last = Cfg.LastWeeklyReset > 0 ? FromUnix(Cfg.LastWeeklyReset).Date : DateTime.Now.Date;
            int days = ((int)DayOfWeek.Monday - (int)last.DayOfWeek + 7) % 7;
            return last.AddDays(days == 0 ? 7 : days);
        }

        private async void OnMinutePassed()
        {
            TryRegisterMenus();
            if (!_configLoaded || _maintenanceRunning) return;
            _maintenanceRunning = true;
            try
            {
                if (Cfg.LastWeeklyReset <= 0)
                {
                    Cfg.LastWeeklyReset = Now;
                    await SaveConfig();
                }
                else if (DateTime.Now >= NextWeeklyReset())
                {
                    await WeeklyReset(null);
                }
            }
            catch (Exception e)
            {
                Logger.LogError("LorisCasino", $"Maintenance : {e.Message}");
            }
            finally
            {
                _maintenanceRunning = false;
            }
        }

        private async Task WeeklyReset(string by)
        {
            List<CasinoClient> all = await CasinoClient.QueryAll();
            List<CasinoClient> clients = all.Select(c => _clients.TryGetValue(c.CharacterId, out CasinoClient cached) ? cached : c).ToList();
            string podium = string.Join("\n", clients.Where(c => c.WeeklyBet > 0)
                .OrderByDescending(c => c.WeeklyWon - c.WeeklyBet).Take(3)
                .Select((c, i) => $"{i + 1}. {c.Name} : {c.WeeklyWon - c.WeeklyBet:+#;-#;0} (mises {c.WeeklyBet})"));

            foreach (CasinoClient client in clients)
            {
                if (client.WeeklyBet == 0 && client.WeeklyWon == 0) continue;
                client.WeeklyBet = 0;
                client.WeeklyWon = 0;
                await client.Save();
            }
            Cfg.LastWeeklyReset = Now;
            await SaveConfig();

            Logger.LogSuccess("LorisCasino", "classement hebdomadaire remis à zéro");
            Log("Reset hebdomadaire", by == null ? "Remise à zéro automatique du classement." : $"Remise à zéro par {by}.",
                ("Podium de la semaine", string.IsNullOrEmpty(podium) ? "Aucun joueur" : podium));
        }

        // ==================================================================
        //  Points du casino (Administration > Points bleus)
        // ==================================================================

        private static string PointLabel(int type, string gameKey)
        {
            switch ((PointKind)type)
            {
                case PointKind.Games:
                    return !string.IsNullOrEmpty(gameKey) && Enum.TryParse(gameKey, out GameId game) ? $"Jeu : {GameNames[game]}" : "Menu des jeux";
                case PointKind.Shop: return "Boutique";
                case PointKind.Daily: return "Récompense quotidienne";
                case PointKind.Employee: return "Espace employé";
                case PointKind.MainMenu: return "Menu principal";
                default: return "?";
            }
        }

        public async void PointsMenu(Player player)
        {
            if (!CheckAdmin(player)) return;
            List<CasinoPoint> models = await CasinoPoint.QueryAll();
            Panel panel = NewPanel(player, "Points du casino", UIPanel.PanelType.Tab, () => PointsMenu(player));

            if (models.Count == 0) Info(panel, "Aucun modèle : créez-en un avec « Nouveau point ».");
            foreach (CasinoPoint model in models)
            {
                panel.AddTabLine($"[{model.Id}] {model.PatternName} {Color($"({PointLabel(model.PointType, model.GameKey)})", Colors.Grey)}", async _ =>
                {
                    model.TypeName = nameof(CasinoPoint);
                    model.Context = this;
                    if (await PointHelper.CreateNPoint(player, model))
                    {
                        Notify(player, $"Point « {model.PatternName} » placé à votre position.", NotificationManager.Type.Success);
                        LogStaff(player, $"Point « {model.PatternName} » placé");
                    }
                    else
                    {
                        Notify(player, "Erreur lors de la création du point.", NotificationManager.Type.Error);
                    }
                });
            }

            if (models.Count > 0) panel.AddButton("Placer ici", _ => panel.SelectTab());
            panel.NextButton("Nouveau point", () => NewPointType(player));
            panel.NextButton("Modèles", () => PointModelsPanel(player));
            panel.NextButton("Points placés", () => PlacedPointsPanel(player));
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        public void NewPointType(Player player)
        {
            if (!CheckAdmin(player)) return;
            Panel panel = NewPanel(player, "Nouveau point", UIPanel.PanelType.Tab, () => NewPointType(player));

            Link(panel, "Menu principal du casino", () => NewPointName(player, PointKind.MainMenu, null));
            Link(panel, "Menu des jeux (tous les jeux)", () => NewPointName(player, PointKind.Games, null));
            foreach (GameId game in GameNames.Keys)
                Link(panel, $"Jeu : {GameNames[game]}", () => NewPointName(player, PointKind.Games, game.ToString()));
            Link(panel, "Boutique", () => NewPointName(player, PointKind.Shop, null));
            Link(panel, "Récompense quotidienne", () => NewPointName(player, PointKind.Daily, null));
            Link(panel, "Espace employé", () => NewPointName(player, PointKind.Employee, null));

            SelectButton(panel);
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private void NewPointName(Player player, PointKind kind, string gameKey)
        {
            string label = PointLabel((int)kind, gameKey);
            Panel panel = NewPanel(player, "Nom du point", UIPanel.PanelType.Input, () => NewPointName(player, kind, gameKey));
            panel.TextLines.Add($"{Color("Type :", Colors.Info)} {label}");
            panel.TextLines.Add("Nom du modèle (laisser vide pour le nom par défaut).");
            panel.SetInputPlaceholder(label);

            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                CasinoPoint model = new CasinoPoint(false)
                {
                    PatternName = string.IsNullOrEmpty(name) ? label : name,
                    PointType = (int)kind,
                    GameKey = gameKey,
                };
                if (!await model.Save())
                {
                    Notify(player, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                    return false;
                }
                Notify(player, $"Modèle « {model.PatternName} » créé. Choisissez-le puis « Placer ici ».", NotificationManager.Type.Success);
                LogStaff(player, $"Modèle de point « {model.PatternName} » ({label}) créé");
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        public async void PointModelsPanel(Player player)
        {
            if (!CheckAdmin(player)) return;
            List<CasinoPoint> models = await CasinoPoint.QueryAll();
            string action = "";
            Panel panel = NewPanel(player, "Modèles de points", UIPanel.PanelType.Tab, () => PointModelsPanel(player));

            if (models.Count == 0) Info(panel, "Aucun modèle");
            foreach (CasinoPoint model in models)
            {
                panel.AddTabLine($"[{model.Id}] {model.PatternName} {Color($"({PointLabel(model.PointType, model.GameKey)})", Colors.Grey)}", async _ =>
                {
                    model.TypeName = nameof(CasinoPoint);
                    model.Context = this;
                    if (action == "rename")
                    {
                        Leave(panel);
                        RenamePointModel(player, model);
                    }
                    else if (action == "delete")
                    {
                        await PointHelper.DeleteNPointsByPattern(player, model);
                        if (await model.Delete())
                        {
                            Notify(player, $"Modèle « {model.PatternName} » et ses points supprimés.", NotificationManager.Type.Success);
                            LogStaff(player, $"Modèle de point « {model.PatternName} » supprimé");
                        }
                        else
                        {
                            Notify(player, "Erreur lors de la suppression.", NotificationManager.Type.Error);
                        }
                        panel.Refresh();
                    }
                });
            }

            if (models.Count > 0)
            {
                panel.AddButton("Renommer", _ => { action = "rename"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        private void RenamePointModel(Player player, CasinoPoint model)
        {
            Panel panel = NewPanel(player, "Renommer un modèle", UIPanel.PanelType.Input, () => RenamePointModel(player, model));
            panel.TextLines.Add($"Nouveau nom pour « {model.PatternName} »");
            panel.SetInputPlaceholder(model.PatternName ?? "");
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    Notify(player, "Le nom ne peut pas être vide.", NotificationManager.Type.Error);
                    return false;
                }
                model.PatternName = name;
                if (!await model.Save())
                {
                    Notify(player, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                    return false;
                }
                Notify(player, $"Modèle renommé en « {name} ».", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }

        public async void PlacedPointsPanel(Player player)
        {
            if (!CheckAdmin(player)) return;
            List<NPoint> points = await NPoint.Query(p => p.TypeName == nameof(CasinoPoint));
            Dictionary<int, CasinoPoint> models = (await CasinoPoint.QueryAll()).ToDictionary(m => m.Id);
            string action = "";
            Panel panel = NewPanel(player, "Points placés", UIPanel.PanelType.Tab, () => PlacedPointsPanel(player));

            if (points.Count == 0) Info(panel, "Aucun point placé");
            foreach (NPoint point in points)
            {
                string name = models.TryGetValue(point.PatternId, out CasinoPoint m) ? $"{m.PatternName} ({PointLabel(m.PointType, m.GameKey)})" : "?";
                panel.AddTabLine($"Point #{point.Id} - {name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            PointHelper.PlayerSetPositionToNPoint(player, point);
                            break;
                        case "move":
                            if (await PointHelper.SetNPointPosition(player, point))
                                Notify(player, "Point déplacé à votre position.", NotificationManager.Type.Success);
                            break;
                        case "delete":
                            await PointHelper.DeleteNPoint(point);
                            Notify(player, $"Point #{point.Id} supprimé.", NotificationManager.Type.Success);
                            LogStaff(player, $"Point #{point.Id} ({name}) supprimé");
                            panel.Refresh();
                            break;
                    }
                });
            }

            if (points.Count > 0)
            {
                panel.AddButton("Se téléporter", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton("Déplacer ici", _ => { action = "move"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            Show(panel);
        }
    }

    // ======================================================================
    //  Types
    // ======================================================================

    public enum GameId
    {
        PileOuFace,
        Roulette,
        MachineASous,
        Blackjack,
        Ticket,
        De,
        Coffre,
        Multiplicateur,
    }

    public enum PointKind
    {
        Games = 1,
        Shop = 2,
        Daily = 3,
        Employee = 4,
        MainMenu = 5,
    }

    public class GameSettings
    {
        public bool Enabled { get; set; } = true;
        public int MinBet { get; set; } = 10;
        public int MaxBet { get; set; } = LorisCasinoPlugin.HardMaxBet;
        /// <summary>Prix fixe (ticket à gratter, coffre).</summary>
        public int Price { get; set; } = 20;
        /// <summary>Gain du coffre gagnant.</summary>
        public int Prize { get; set; } = 50;
    }

    // ======================================================================
    //  Base de données (ORM ModKit)
    // ======================================================================

    /// <summary>Configuration du casino (une seule ligne), modifiable en jeu.</summary>
    public class CasinoConfig : ModEntity<CasinoConfig>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PanelTitle { get; set; } = "Loris Casino";
        public int AdminLevel { get; set; } = 1;
        public int CasinoBizId { get; set; }
        public double TokenPrice { get; set; } = 1;
        public int ResalePercent { get; set; } = 90;
        public int DailyReward { get; set; } = 25;
        public bool ShopResale { get; set; } = true;
        public bool MenuAnywhere { get; set; }
        public string GamesJson { get; set; }
        public bool WebhookEnabled { get; set; }
        public string WebhookUrl { get; set; } = "";
        public string WebhookColor { get; set; } = "#9B59B6";
        public long LastWeeklyReset { get; set; }

        [Ignore]
        public Dictionary<string, GameSettings> Games { get; set; } = new Dictionary<string, GameSettings>();

        public GameSettings GetGame(GameId game)
        {
            string key = game.ToString();
            if (!Games.TryGetValue(key, out GameSettings settings) || settings == null)
            {
                settings = new GameSettings();
                if (game == GameId.MachineASous) settings.MinBet = 5;
                Games[key] = settings;
            }
            return settings;
        }

        public void LoadGames()
        {
            try
            {
                Games = string.IsNullOrEmpty(GamesJson)
                    ? new Dictionary<string, GameSettings>()
                    : JsonConvert.DeserializeObject<Dictionary<string, GameSettings>>(GamesJson) ?? new Dictionary<string, GameSettings>();
            }
            catch (JsonException)
            {
                Games = new Dictionary<string, GameSettings>();
            }
            if (string.IsNullOrWhiteSpace(PanelTitle)) PanelTitle = "Loris Casino";
            if (string.IsNullOrWhiteSpace(WebhookColor)) WebhookColor = "#9B59B6";
        }

        public void StoreGames()
        {
            foreach (GameId game in Enum.GetValues(typeof(GameId))) GetGame(game);
            GamesJson = JsonConvert.SerializeObject(Games);
        }
    }

    /// <summary>Compte casino d'un personnage : jetons et statistiques.</summary>
    public class CasinoClient : ModEntity<CasinoClient>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int CharacterId { get; set; }
        public string Name { get; set; }
        public int Tokens { get; set; }
        public long WeeklyBet { get; set; }
        public long WeeklyWon { get; set; }
        public long TotalBet { get; set; }
        public long TotalWon { get; set; }
        public long LastDaily { get; set; }
    }

    /// <summary>Membre du staff casino (en plus des admins et du patron de l'entreprise).</summary>
    public class CasinoStaff : ModEntity<CasinoStaff>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int CharacterId { get; set; }
        public string Name { get; set; }
        public string AddedBy { get; set; }
        public long AddedAt { get; set; }
    }

    /// <summary>
    /// Modèle de point du casino. Chaque placement crée un point bleu (NPoint ModKit)
    /// qui ouvre, selon son type : jeux (ou un jeu précis), boutique, récompense
    /// quotidienne, espace employé ou menu principal.
    /// </summary>
    public class CasinoPoint : ModEntity<CasinoPoint>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }
        public int PointType { get; set; } = (int)PointKind.Games;
        public string GameKey { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(CasinoPoint);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public CasinoPoint() { }

        public CasinoPoint(bool isCreated)
        {
            TypeName = nameof(CasinoPoint);
        }

        public void OnPlayerTrigger(Player player)
        {
            LorisCasinoPlugin.Instance?.OnPointTriggered(player, PointType, GameKey);
        }

        public async Task SetProperties(int id)
        {
            CasinoPoint result = await Query(id);
            Id = id;
            TypeName = nameof(CasinoPoint);
            PatternName = result?.PatternName;
            PointType = result?.PointType ?? 0;
            GameKey = result?.GameKey;
        }

        public void CreateOrGenerate(Player player) => LorisCasinoPlugin.Instance?.PointsMenu(player);

        public void SetPatternData(Player player) => LorisCasinoPlugin.Instance?.NewPointType(player);

        public Task GetPatternData(Player player, bool forEdit)
        {
            LorisCasinoPlugin.Instance?.PointModelsPanel(player);
            return Task.CompletedTask;
        }

        public Task GetNPoints(Player player)
        {
            LorisCasinoPlugin.Instance?.PlacedPointsPanel(player);
            return Task.CompletedTask;
        }
    }
}
