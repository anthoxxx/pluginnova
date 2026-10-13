using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.BizSystem;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.DiscordHelper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.Utils;
using static ModKit.Helper.TextFormattingHelper;

namespace Boulangerie
{
    /// <summary>
    /// Boulangerie by Loriss Strange : métier de boulanger avec caisse enregistreuse
    /// (point bleu), produits et formules configurables, paiement espèces / carte
    /// et journal des ventes sur Discord.
    /// </summary>
    public partial class BoulangeriePlugin : ModKit.ModKit
    {
        public const string Tag = "Boulangerie";

        public static BoulangeriePlugin Instance { get; private set; }

        /// <summary>Configuration globale (chargée depuis la base au démarrage).</summary>
        public static BakeryConfig Config { get; private set; } = new BakeryConfig();

        /// <summary>Panier en cours de chaque boulanger, par id de personnage.</summary>
        public readonly Dictionary<int, Cart> Carts = new Dictionary<int, Cart>();

        public BoulangeriePlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "Loriss Strange");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            Instance = this;

            Orm.RegisterTable<BakeryConfig>();
            Orm.RegisterTable<CashRegister>();
            Orm.RegisterTable<BakeryProduct>();
            Orm.RegisterTable<BakeryFormula>();
            Orm.RegisterTable<BakerySale>();
            _ = LoadConfig();

            CashRegister pattern = new CashRegister(false);
            PointHelper.AddPattern(nameof(CashRegister), pattern);

            if (AAMenu.AAMenu.menu != null)
            {
                // Administration > Points : placer / gérer les caisses
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "Caisse boulangerie", pattern, this);
                // Administration > Plugins : configuration du plugin
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 1, "Boulangerie", ui =>
                {
                    Player player = FindPlayer(ui.playerId);
                    if (player != null) ConfigPanel(player);
                });
                // Métier : gestion de la carte par le patron
                AAMenu.Menu.AddBizTabLine(PluginInformations, new List<Activity.Type> { Activity.Type.None }, null, "Boulangerie", ui =>
                {
                    Player player = FindPlayer(ui.playerId);
                    if (player != null) BizMenu(player);
                });
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez la commande /boulangerie.");
            }

            RegisterCommands();
            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé (by Loriss Strange)");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            PointHelper.InitAllNPoint(player);
        }

        /// <summary>Charge (ou crée) la ligne de configuration. Réessaie le temps que la table soit créée.</summary>
        private static async Task LoadConfig()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(1000);
                try
                {
                    List<BakeryConfig> configs = await BakeryConfig.QueryAll();
                    if (configs.Count > 0)
                    {
                        Config = configs[0];
                    }
                    else
                    {
                        Config = new BakeryConfig();
                        await Config.Save();
                    }
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == 9) Logger.LogError(Tag, "Impossible de charger la configuration : " + ex.Message);
                }
            }
        }

        private void RegisterCommands()
        {
            new SChatCommand("/boulangerie", "Configuration du plugin Boulangerie (staff)", "/boulangerie",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!IsStaff(player))
                    {
                        player.Notify(Tag, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    ConfigPanel(player);
                })).Register();

            new SChatCommand("/caisses", "Placer / gérer les caisses de boulangerie (staff)", "/caisses",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!IsStaff(player))
                    {
                        player.Notify(Tag, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    new CashRegister(false) { Context = this }.CreateOrGenerate(player);
                })).Register();
        }

        // ------------------------------------------------------------------
        //  Utilitaires
        // ------------------------------------------------------------------

        /// <summary>Joueur connecté à partir de son netId (null s'il s'est déconnecté).</summary>
        public static Player FindPlayer(uint netId) => Nova.server.Players.FirstOrDefault(p => p != null && p.netId == netId);

        public static bool IsStaff(Player player) => player.IsAdmin && player.serviceAdmin;

        public static string Money(double amount) => amount.ToString("0.00", CultureInfo.InvariantCulture) + "€";

        public static bool TryParseAmount(string text, out double amount)
        {
            amount = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Replace("€", "").Replace(',', '.').Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out amount)
                   && amount >= 0 && !double.IsInfinity(amount);
        }

        public static string BizName(int bizId)
        {
            Bizs biz = Nova.biz.FetchBiz(bizId);
            return biz != null ? BizUtils.BizNameWithoutId(biz.BizName) : $"Entreprise #{bizId}";
        }

        public static string ItemName(int itemId)
        {
            if (itemId <= 0) return "Aucun";
            Life.InventorySystem.Item item = ItemUtils.GetItemById(itemId);
            return item != null ? item.itemName : $"Item #{itemId} (introuvable)";
        }

        public static int Icon(int itemId) => itemId > 0 ? ItemUtils.GetIconIdByItemId(itemId) : IconUtils.Others.None.Id;

        public static string OnOff(bool value) => value ? Color("Oui", Colors.Success) : Color("Non", Colors.Error);

        /// <summary>Le joueur peut-il modifier la carte (produits / formules) de cette entreprise ?</summary>
        public static bool CanManage(Player player, int bizId)
        {
            if (IsStaff(player)) return true;
            if (!player.HasBiz || player.biz.Id != bizId) return false;
            return PermissionUtils.PlayerIsOwner(player) || Config.EmployeesCanEditMenu;
        }

        /// <summary>Envoie un embed sur le webhook de la caisse, ou sur celui de la configuration.</summary>
        public static void SendWebhook(string registerWebhook, string title, string description, List<string> names, List<string> values)
        {
            string url = !string.IsNullOrWhiteSpace(registerWebhook) ? registerWebhook : Config.WebhookUrl;
            if (!Config.WebhookEnabled || string.IsNullOrWhiteSpace(url)) return;

            string color = string.IsNullOrWhiteSpace(Config.WebhookColor) ? "#E8A33D" : Config.WebhookColor;
            // Discord refuse les champs vides
            values = values.Select(v => string.IsNullOrWhiteSpace(v) ? "-" : v).ToList();
            Task.Run(async () =>
            {
                try
                {
                    await DiscordHelper.SendEmbed(new DiscordWebhookClient(url.Trim()), color, title,
                        string.IsNullOrWhiteSpace(description) ? "-" : description,
                        names, values, true, true, "Boulangerie by Loriss Strange");
                }
                catch (Exception ex)
                {
                    Logger.LogError(Tag, "Webhook Discord : " + ex.Message);
                }
            });
        }

        /// <summary>Panel de saisie simple : retourne au panel précédent si <paramref name="onValidate"/> renvoie vrai.</summary>
        public void AskInput(Player player, string title, string text, string placeholder, Func<string, Task<bool>> onValidate)
        {
            Panel panel = PanelHelper.Create(title, UIPanel.PanelType.Input, player, () => AskInput(player, title, text, placeholder, onValidate));
            panel.TextLines.Add(text);
            panel.SetInputPlaceholder(placeholder ?? "");
            panel.PreviousButtonWithAction("Valider", async () => await onValidate(panel.inputText?.Trim() ?? ""));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Saisie d'un nombre entier borné.</summary>
        public void AskInt(Player player, string title, string text, int current, int min, int max, Func<int, Task> onValidate)
        {
            AskInput(player, title, $"{text}<br>Valeur actuelle : {current} (entre {min} et {max})", current.ToString(), async input =>
            {
                if (!int.TryParse(input, out int value) || value < min || value > max)
                {
                    player.Notify(Tag, $"Saisissez un nombre entre {min} et {max}.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                await onValidate(value);
                return true;
            });
        }

        /// <summary>Liste des entreprises du serveur : <paramref name="onChosen"/> gère la suite.</summary>
        public void ChooseBiz(Player player, string title, IEnumerable<int> onlyBizIds, Func<Panel, Bizs, Task> onChosen)
        {
            List<Bizs> bizs = Nova.biz.bizs
                .Where(b => onlyBizIds == null || onlyBizIds.Contains(b.Id))
                .OrderBy(b => b.Id)
                .ToList();

            Panel panel = PanelHelper.Create(title, UIPanel.PanelType.Tab, player, () => ChooseBiz(player, title, onlyBizIds, onChosen));
            if (bizs.Count == 0)
                panel.AddTabLine("Aucune entreprise", _ => panel.Refresh());
            foreach (Bizs biz in bizs)
            {
                Bizs b = biz;
                panel.AddTabLine($"[{b.Id}] {BizUtils.BizNameWithoutId(b.BizName)}", async _ => await onChosen(panel, b));
            }
            if (bizs.Count > 0)
                panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  AAMenu : Métier > Boulangerie
        // ------------------------------------------------------------------

        private async void BizMenu(Player player)
        {
            if (!player.HasBiz)
            {
                player.Notify(Tag, "Vous n'avez pas d'entreprise.", NotificationManager.Type.Error, 5f);
                return;
            }
            int bizId = player.biz.Id;
            List<CashRegister> registers = await CashRegister.Query(r => r.BizId == bizId);
            if (registers.Count == 0 && !IsStaff(player))
            {
                player.Notify(Tag, "Votre entreprise n'a aucune caisse de boulangerie.", NotificationManager.Type.Warning, 5f);
                return;
            }
            if (!CanManage(player, bizId))
            {
                player.Notify(Tag, "Seul le patron peut gérer la carte de la boulangerie.", NotificationManager.Type.Error, 5f);
                return;
            }
            ManageMenu(player, bizId, true);
        }

        // ------------------------------------------------------------------
        //  AAMenu : Administration > Plugins > Boulangerie
        // ------------------------------------------------------------------

        public void ConfigPanel(Player player)
        {
            if (!IsStaff(player))
            {
                player.Notify(Tag, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                return;
            }

            BakeryConfig c = Config;
            const string edit = "Boulangerie - Réglage";
            Panel panel = PanelHelper.Create("Boulangerie - Configuration", UIPanel.PanelType.TabPrice, player, () => ConfigPanel(player));
            int none = IconUtils.Others.None.Id;

            panel.AddTabLine("Webhook Discord (URL)", string.IsNullOrWhiteSpace(c.WebhookUrl) ? Color("Non défini", Colors.Error) : Color("Défini", Colors.Success), none, _ =>
                AskInput(player, edit, "URL du webhook Discord (laisser vide pour retirer)", c.WebhookUrl, async input =>
                {
                    if (input.Length > 0 && !input.StartsWith("https://"))
                    {
                        player.Notify(Tag, "L'URL doit commencer par https://", NotificationManager.Type.Error, 5f);
                        return false;
                    }
                    c.WebhookUrl = input;
                    if (input.Length > 0) c.WebhookEnabled = true;
                    return await c.Save();
                }));
            panel.AddTabLine("Webhook activé", OnOff(c.WebhookEnabled), none, async _ => { c.WebhookEnabled = !c.WebhookEnabled; await c.Save(); panel.Refresh(); });
            panel.AddTabLine("Couleur des embeds", Color(c.WebhookColor, c.WebhookColor), none, _ =>
                AskInput(player, edit, "Couleur des messages Discord, format #RRGGBB", c.WebhookColor, async input =>
                {
                    string hex = input.StartsWith("#") ? input : "#" + input;
                    if (hex.Length != 7 || !int.TryParse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int _))
                    {
                        player.Notify(Tag, "Couleur invalide (ex. #E8A33D).", NotificationManager.Type.Error, 5f);
                        return false;
                    }
                    c.WebhookColor = hex.ToUpperInvariant();
                    return await c.Save();
                }));
            panel.AddTabLine("Tester le webhook", "", none, _ =>
            {
                if (string.IsNullOrWhiteSpace(c.WebhookUrl) || !c.WebhookEnabled)
                    player.Notify(Tag, "Définissez et activez d'abord le webhook.", NotificationManager.Type.Warning, 5f);
                else
                {
                    SendWebhook(null, "🥐 Test du webhook", $"Test envoyé par {player.FullName}. Le webhook fonctionne !",
                        new List<string> { "Serveur" }, new List<string> { Nova.serverInfo?.serverName ?? "Nova-Life" });
                    player.Notify(Tag, "Message de test envoyé.", NotificationManager.Type.Success, 5f);
                }
                panel.Refresh();
            });
            panel.AddTabLine("Distance max avec le client", $"{c.MaxClientDistance:0.#} m", none, _ =>
                AskInt(player, edit, "Distance maximale (m) pour encaisser un client", (int)c.MaxClientDistance, 1, 50, async v => { c.MaxClientDistance = v; await c.Save(); }));
            panel.AddTabLine("Commission du boulanger", $"{c.CommissionPercent}%", none, _ =>
                AskInt(player, edit, "Part de chaque vente versée sur le compte du boulanger (le reste va à l'entreprise)", c.CommissionPercent, 0, 100, async v => { c.CommissionPercent = v; await c.Save(); }));
            panel.AddTabLine("Remise maximale", $"{c.MaxDiscountPercent}%", none, _ =>
                AskInt(player, edit, "Remise maximale qu'un boulanger peut appliquer sur un panier", c.MaxDiscountPercent, 0, 100, async v => { c.MaxDiscountPercent = v; await c.Save(); }));
            panel.AddTabLine("Délai de paiement", $"{c.PaymentTimeout} s", none, _ =>
                AskInt(player, edit, "Temps laissé au client pour payer (secondes)", c.PaymentTimeout, 10, 600, async v => { c.PaymentTimeout = v; await c.Save(); }));
            panel.AddTabLine("Prise de service obligatoire", OnOff(c.RequireService), none, async _ => { c.RequireService = !c.RequireService; await c.Save(); panel.Refresh(); });
            panel.AddTabLine("Donner les items au client", OnOff(c.GiveItems), none, async _ => { c.GiveItems = !c.GiveItems; await c.Save(); panel.Refresh(); });
            panel.AddTabLine("Prendre les items dans l'inventaire du boulanger", OnOff(c.UseCashierStock), none, async _ => { c.UseCashierStock = !c.UseCashierStock; await c.Save(); panel.Refresh(); });
            panel.AddTabLine("Les employés peuvent modifier la carte", OnOff(c.EmployeesCanEditMenu), none, async _ => { c.EmployeesCanEditMenu = !c.EmployeesCanEditMenu; await c.Save(); panel.Refresh(); });
            panel.AddTabLine(Color("Gérer la carte d'une boulangerie", Colors.Info), "", none, async _ =>
            {
                List<int> bizIds = (await CashRegister.QueryAll()).Select(r => r.BizId).Distinct().ToList();
                if (bizIds.Count == 0)
                {
                    player.Notify(Tag, "Aucune caisse n'est encore liée à une entreprise.", NotificationManager.Type.Warning, 5f);
                    panel.Refresh();
                    return;
                }
                ChooseBiz(player, "Boulangerie - Choisir la boulangerie", bizIds, (p, biz) => { player.ClosePanel(p); ManageMenu(player, biz.Id); return Task.CompletedTask; });
            });
            panel.AddTabLine(Color("Placer / gérer les caisses", Colors.Info), "", none, _ => new CashRegister(false) { Context = this }.CreateOrGenerate(player));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            if (AAMenu.AAMenu.menu != null)
                panel.AddButton("Retour", _ => { panel.Close(); AAMenu.AAMenu.menu.AdminPluginPanel(player); });
            panel.CloseButton();
            panel.Display();
        }
    }
}
