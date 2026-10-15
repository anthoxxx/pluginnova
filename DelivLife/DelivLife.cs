using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.BizSystem;
using Life.DB;
using Life.InventorySystem;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.DiscordHelper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using Newtonsoft.Json;
using SQLite;
using static ModKit.Helper.TextFormattingHelper;

namespace DelivLife
{
    /// <summary>
    /// DelivLife by Loriss Strange.
    /// Restaurant clé en main : bornes de commande pour les clients (comme dans un fast-food),
    /// caisse enregistreuse et suivi des commandes pour les employés, carte configurable
    /// (plats, boissons, formules...), tickets numérotés, paiement espèces / carte et webhook Discord.
    /// </summary>
    public partial class DelivLifePlugin : ModKit.ModKit
    {
        public const string Tag = "DelivLife";
        public const string Author = "Loriss Strange";

        public static DelivLifePlugin Instance { get; private set; }

        /// <summary>Paniers en cours (clé : personnage + borne/caisse).</summary>
        private readonly Dictionary<string, DelivCart> carts = new Dictionary<string, DelivCart>();

        /// <summary>Employés en service (id de personnage).</summary>
        private readonly HashSet<int> onDuty = new HashSet<int>();

        /// <summary>Demandes de paiement envoyées par la caisse (clé : id de personnage du client).</summary>
        private readonly Dictionary<int, DelivPaymentRequest> paymentRequests = new Dictionary<int, DelivPaymentRequest>();

        public DelivLifePlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", Author);
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            Instance = this;

            Orm.RegisterTable<DelivRestaurant>();
            Orm.RegisterTable<DelivProduct>();
            Orm.RegisterTable<DelivOrder>();
            Orm.RegisterTable<DelivKiosk>();
            Orm.RegisterTable<DelivCounter>();

            DelivKiosk kiosk = new DelivKiosk(false);
            DelivCounter counter = new DelivCounter(false);
            PointHelper.AddPattern(nameof(DelivKiosk), kiosk);
            PointHelper.AddPattern(nameof(DelivCounter), counter);

            if (AAMenu.AAMenu.menu != null)
            {
                // Administration > Points bleus
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "DelivLife - Points clients", kiosk, this);
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "DelivLife - Points employés", counter, this);

                // Métier : visible dans toutes les sociétés, accès vérifié à l'ouverture
                AAMenu.Menu.AddBizTabLine(PluginInformations, new List<Activity.Type> { Activity.Type.None }, null,
                    Color("DelivLife", Colors.Orange), ui => EmployeeEntry(PanelHelper.ReturnPlayerFromPanel(ui)));

                // Administration > Plugins
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 1, "DelivLife",
                    ui => AdminHome(PanelHelper.ReturnPlayerFromPanel(ui)));
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez /delivlife et /delivadmin.");
            }

            RegisterCommands();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version} by {Author}", "initialisé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            PointHelper.InitAllNPoint(player);
        }

        public override void OnPlayerDisconnect(NetworkConnection conn)
        {
            try
            {
                Player player = Nova.server.Players.FirstOrDefault(p => p != null && p.conn == conn);
                if (player?.character != null)
                {
                    int id = player.character.Id;
                    onDuty.Remove(id);
                    paymentRequests.Remove(id);
                    carts.Remove(CartKey(player, false));
                    carts.Remove(CartKey(player, true));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(Tag, ex.Message);
            }
            base.OnPlayerDisconnect(conn);
        }

        private void RegisterCommands()
        {
            new SChatCommand("/delivlife", new[] { "/dl" }, "Menu employé DelivLife", "/delivlife",
                (Action<Player, string[]>)((player, args) => EmployeeEntry(player))).Register();

            new SChatCommand("/commande", new[] { "/macommande" }, "Suivre mes commandes DelivLife", "/commande",
                (Action<Player, string[]>)((player, args) => ClientTracking(player, 0))).Register();

            new SChatCommand("/delivadmin", "Administration DelivLife (staff)", "/delivadmin",
                (Action<Player, string[]>)((player, args) => AdminHome(player))).Register();
        }

        // ==================================================================
        //  Utilitaires
        // ==================================================================

        public static string Money(double amount)
        {
            return amount.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',') + " €";
        }

        public static bool TryParsePrice(string input, out double price)
        {
            price = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;
            string clean = input.Replace("€", "").Replace(',', '.').Trim();
            if (!double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out price)) return false;
            price = Math.Round(price, 2);
            return price >= 0 && price < 1000000;
        }

        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static int Today() => int.Parse(DateTime.Now.ToString("yyyyMMdd"));

        public static string Hour(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("HH:mm");

        public static string Elapsed(long unix)
        {
            long minutes = Math.Max(0, (Now() - unix) / 60);
            if (minutes < 1) return "à l'instant";
            if (minutes < 60) return $"il y a {minutes} min";
            return $"il y a {minutes / 60} h {minutes % 60:00}";
        }

        public static void Notify(Player player, string message, NotificationManager.Type type = NotificationManager.Type.Info)
        {
            player?.Notify(Tag, message, type, 6f);
        }

        private static bool IsStaff(Player player) => player != null && player.IsAdmin && player.serviceAdmin;

        private static string CartKey(Player player, bool register) => $"{player.character.Id}:{(register ? "caisse" : "borne")}";

        private DelivCart GetCart(Player player, DelivRestaurant restaurant, bool register)
        {
            string key = CartKey(player, register);
            if (!carts.TryGetValue(key, out DelivCart cart) || cart.BizId != restaurant.BizId)
            {
                cart = new DelivCart { BizId = restaurant.BizId };
                carts[key] = cart;
            }
            return cart;
        }

        private void ClearCart(Player player, bool register) => carts.Remove(CartKey(player, register));

        private static async Task<DelivRestaurant> GetRestaurantByBiz(int bizId)
        {
            List<DelivRestaurant> restaurants = await DelivRestaurant.Query(r => r.BizId == bizId);
            return restaurants?.FirstOrDefault();
        }

        private static async Task<List<DelivProduct>> GetProducts(int bizId, bool onlyAvailable)
        {
            List<DelivProduct> products = await DelivProduct.Query(p => p.BizId == bizId);
            return products
                .Where(p => !onlyAvailable || p.IsAvailable)
                .OrderBy(p => p.Category)
                .ThenBy(p => p.Name)
                .ToList();
        }

        /// <summary>Joueurs connectés membres de l'entreprise.</summary>
        private static IEnumerable<Player> BizMembersOnline(int bizId)
        {
            return Nova.server.GetAllInGamePlayers().Where(p => p?.character != null && p.character.BizId == bizId);
        }

        private bool HasStaffOnDuty(int bizId) => BizMembersOnline(bizId).Any(p => onDuty.Contains(p.character.Id));

        /// <summary>Prévient les employés en service (ou tous les employés connectés si personne n'est en service).</summary>
        private void NotifyEmployees(int bizId, string message, NotificationManager.Type type = NotificationManager.Type.Info)
        {
            List<Player> members = BizMembersOnline(bizId).ToList();
            List<Player> targets = members.Where(p => onDuty.Contains(p.character.Id)).ToList();
            if (targets.Count == 0) targets = members;
            foreach (Player p in targets)
            {
                p.Notify(Tag, message, type, 8f);
                p.SendText($"<color=#f5a742>[{Tag}]</color> {message}");
            }
        }

        private static Player GetOnlineCharacter(int characterId)
        {
            if (characterId <= 0) return null;
            return Nova.server.GetAllInGamePlayers().FirstOrDefault(p => p?.character != null && p.character.Id == characterId);
        }

        private async Task<bool> IsManager(Player player, DelivRestaurant restaurant)
        {
            if (IsStaff(player)) return true;
            if (!player.HasBiz || player.biz == null || player.biz.Id != restaurant.BizId) return false;
            if (PermissionUtils.PlayerIsOwner(player)) return true;
            return await PermissionUtils.PlayerCanManageTheBank(player);
        }

        private static void SendWebhook(DelivRestaurant restaurant, string color, string title, string description,
            List<string> names, List<string> values)
        {
            if (restaurant == null || string.IsNullOrWhiteSpace(restaurant.WebhookUrl)) return;
            DiscordWebhookClient client = new DiscordWebhookClient(restaurant.WebhookUrl.Trim());
            Task.Run(async () =>
            {
                try
                {
                    await DiscordHelper.SendEmbed(client, color, title, description, names, values, true, true, $"{Tag} by {Author}");
                }
                catch (Exception ex)
                {
                    Logger.LogError(Tag, "Webhook : " + ex.Message);
                }
            });
        }

        private static void SendOrderWebhook(DelivRestaurant restaurant, DelivOrder order, string color, string title)
        {
            string lines = string.Join("\n", order.GetLines().Select(l => $"{l.Quantity}x {l.Name}" +
                (string.IsNullOrEmpty(l.Details) ? "" : $" ({l.Details})") + $" — {Money(l.Total)}"));
            if (string.IsNullOrEmpty(lines)) lines = "-";
            if (!string.IsNullOrWhiteSpace(order.Note)) lines += $"\n📝 {order.Note}";

            SendWebhook(restaurant, color, $"{title} — Ticket n°{order.TicketLabel}", lines,
                new List<string> { "Client", "Total", "Paiement", "Mode", "Origine", "Statut", "Employé" },
                new List<string>
                {
                    order.ClientName ?? "-",
                    Money(order.Total),
                    order.PaymentLabel,
                    order.ModeLabel,
                    order.SourceLabel,
                    order.StatusLabel,
                    string.IsNullOrEmpty(order.EmployeeName) ? "-" : order.EmployeeName
                });
        }
    }

    // ======================================================================
    //  Données
    // ======================================================================

    public static class DelivCategory
    {
        public const int Formule = 0;
        public const int Plat = 1;
        public const int Accompagnement = 2;
        public const int Boisson = 3;
        public const int Dessert = 4;

        public static readonly int[] All = { Formule, Plat, Accompagnement, Boisson, Dessert };

        public static string Label(int category)
        {
            switch (category)
            {
                case Formule: return "Formules";
                case Plat: return "Plats";
                case Accompagnement: return "Accompagnements";
                case Boisson: return "Boissons";
                case Dessert: return "Desserts";
                default: return "Autres";
            }
        }

        public static string Singular(int category)
        {
            switch (category)
            {
                case Formule: return "Formule";
                case Plat: return "Plat";
                case Accompagnement: return "Accompagnement";
                case Boisson: return "Boisson";
                case Dessert: return "Dessert";
                default: return "Autre";
            }
        }

        public static int Icon(int category)
        {
            switch (category)
            {
                case Formule: return IconUtils.Foods.Fries.Id;
                case Plat: return IconUtils.Foods.Pizza.Id;
                case Accompagnement: return IconUtils.Foods.Fries.Id;
                case Boisson: return IconUtils.Foods.Soda.Id;
                case Dessert: return IconUtils.Objets.CupOfCoffee.Id;
                default: return IconUtils.Foods.Pizza.Id;
            }
        }
    }

    public static class DelivStatus
    {
        public const int EnAttente = 0;
        public const int EnPreparation = 1;
        public const int Prete = 2;
        public const int Remise = 3;
        public const int Annulee = 4;

        public static string Label(int status)
        {
            switch (status)
            {
                case EnAttente: return "En attente";
                case EnPreparation: return "En préparation";
                case Prete: return "Prête";
                case Remise: return "Remise au client";
                case Annulee: return "Annulée";
                default: return "?";
            }
        }

        public static string Colored(int status)
        {
            switch (status)
            {
                case EnAttente: return Color(Label(status), Colors.Warning);
                case EnPreparation: return Color(Label(status), Colors.Orange);
                case Prete: return Color(Label(status), Colors.Success);
                case Remise: return Color(Label(status), Colors.Grey);
                case Annulee: return Color(Label(status), Colors.Error);
                default: return Label(status);
            }
        }
    }

    /// <summary>Entreprise autorisée à utiliser DelivLife et ses réglages.</summary>
    public class DelivRestaurant : ModEntity<DelivRestaurant>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int BizId { get; set; }
        public string DisplayName { get; set; }
        public string WelcomeMessage { get; set; }
        public bool IsOpen { get; set; } = true;
        public bool AcceptCash { get; set; } = true;
        public bool AcceptCard { get; set; } = true;
        public bool RequireStaffOnDuty { get; set; }
        public bool GiveItems { get; set; } = true;
        public bool TakeItemsFromEmployee { get; set; }
        public string WebhookUrl { get; set; }
        public int TicketCounter { get; set; }
        public int TicketDate { get; set; }

        public string Name => string.IsNullOrWhiteSpace(DisplayName) ? $"Restaurant #{BizId}" : DisplayName;
    }

    /// <summary>Produit de la carte (plat, boisson, formule...).</summary>
    public class DelivProduct : ModEntity<DelivProduct>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int BizId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int Category { get; set; }
        public double Price { get; set; }
        public bool IsAvailable { get; set; } = true;

        /// <summary>Objet du jeu remis au client (0 = aucun).</summary>
        public int ItemId { get; set; }

        /// <summary>Icône personnalisée (-1 = automatique).</summary>
        public int IconId { get; set; } = -1;

        /// <summary>Formules : catégories à choisir, séparées par des virgules (ex. "1,2,3").</summary>
        public string FormulaSlots { get; set; }

        public bool IsFormula => Category == DelivCategory.Formule;

        public List<int> GetSlots()
        {
            if (string.IsNullOrWhiteSpace(FormulaSlots)) return new List<int>();
            return FormulaSlots.Split(',')
                .Select(s => int.TryParse(s.Trim(), out int c) ? c : -1)
                .Where(c => c > DelivCategory.Formule && c <= DelivCategory.Dessert)
                .ToList();
        }

        public void SetSlots(List<int> slots) => FormulaSlots = string.Join(",", slots);

        public int GetIcon()
        {
            if (IconId >= 0) return IconId;
            if (ItemId > 0 && ItemUtils.GetItemById(ItemId) != null) return ItemUtils.GetIconIdByItemId(ItemId);
            return DelivCategory.Icon(Category);
        }
    }

    /// <summary>Ligne de commande (stockée en JSON dans la commande).</summary>
    public class DelivOrderLine
    {
        public int ProductId { get; set; }
        public string Name { get; set; }
        public int Quantity { get; set; }
        public double UnitPrice { get; set; }

        /// <summary>Composition d'une formule (ex. "Burger + Frites + Coca").</summary>
        public string Details { get; set; }

        /// <summary>Produits composant la ligne (le produit lui-même ou les choix de la formule).</summary>
        public List<int> ComponentIds { get; set; } = new List<int>();

        /// <summary>Objets du jeu à remettre pour UNE unité.</summary>
        public List<int> ItemIds { get; set; } = new List<int>();

        [JsonIgnore]
        public double Total => Math.Round(UnitPrice * Quantity, 2);
    }

    public class DelivOrder : ModEntity<DelivOrder>
    {
        public const int PayCash = 0;
        public const int PayCard = 1;
        public const int SourceKiosk = 0;
        public const int SourceRegister = 1;
        public const int ModeOnSite = 0;
        public const int ModeTakeAway = 1;

        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int BizId { get; set; }
        public int Ticket { get; set; }
        public int TicketDate { get; set; }
        public int ClientCharacterId { get; set; }
        public string ClientName { get; set; }
        public string LinesJson { get; set; }
        public double Total { get; set; }
        public int PaymentMethod { get; set; }
        public int Status { get; set; }
        public int Source { get; set; }
        public int Mode { get; set; }
        public string Note { get; set; }
        public string EmployeeName { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }

        public List<DelivOrderLine> GetLines()
        {
            if (string.IsNullOrEmpty(LinesJson)) return new List<DelivOrderLine>();
            try { return JsonConvert.DeserializeObject<List<DelivOrderLine>>(LinesJson) ?? new List<DelivOrderLine>(); }
            catch { return new List<DelivOrderLine>(); }
        }

        public void SetLines(List<DelivOrderLine> lines) => LinesJson = JsonConvert.SerializeObject(lines);

        public bool IsActive => Status == DelivStatus.EnAttente || Status == DelivStatus.EnPreparation || Status == DelivStatus.Prete;
        public string TicketLabel => Ticket.ToString("000");
        public string PaymentLabel => PaymentMethod == PayCard ? "Carte bancaire" : "Espèces";
        public string SourceLabel => Source == SourceRegister ? "Caisse" : "Borne";
        public string ModeLabel => Mode == ModeTakeAway ? "À emporter" : "Sur place";
        public string StatusLabel => DelivStatus.Label(Status);
        public int ItemCount => GetLines().Sum(l => l.Quantity);
    }

    /// <summary>Panier en mémoire (borne ou caisse).</summary>
    public class DelivCart
    {
        public int BizId;
        public int Mode = DelivOrder.ModeOnSite;
        public bool ModeChosen;
        public string Note;
        public List<DelivOrderLine> Lines = new List<DelivOrderLine>();

        public double Total => Math.Round(Lines.Sum(l => l.Total), 2);
        public int Count => Lines.Sum(l => l.Quantity);
        public bool IsEmpty => Lines.Count == 0;

        public void Add(DelivProduct product, int quantity, List<DelivProduct> components = null)
        {
            if (quantity <= 0) return;
            string details = components != null && components.Count > 0 ? string.Join(" + ", components.Select(c => c.Name)) : null;

            DelivOrderLine existing = Lines.FirstOrDefault(l => l.ProductId == product.Id && l.Details == details);
            if (existing != null)
            {
                existing.Quantity += quantity;
                return;
            }

            DelivOrderLine line = new DelivOrderLine
            {
                ProductId = product.Id,
                Name = product.Name,
                Quantity = quantity,
                UnitPrice = product.Price,
                Details = details
            };
            line.ComponentIds.Add(product.Id);
            if (product.ItemId > 0) line.ItemIds.Add(product.ItemId);
            if (components != null)
            {
                foreach (DelivProduct c in components)
                {
                    line.ComponentIds.Add(c.Id);
                    if (c.ItemId > 0) line.ItemIds.Add(c.ItemId);
                }
            }
            Lines.Add(line);
        }

        public DelivCart Clone()
        {
            return new DelivCart
            {
                BizId = BizId,
                Mode = Mode,
                ModeChosen = ModeChosen,
                Note = Note,
                Lines = JsonConvert.DeserializeObject<List<DelivOrderLine>>(JsonConvert.SerializeObject(Lines))
            };
        }
    }

    /// <summary>Demande de paiement envoyée par un employé (caisse) à un client.</summary>
    public class DelivPaymentRequest
    {
        public int EmployeeCharacterId;
        public string EmployeeName;
        public int RestaurantId;
        public DelivCart Cart;
        public long CreatedAt;
        public bool Done;
    }
}

namespace DelivLife
{
    // ======================================================================
    //  CÔTÉ CLIENT : borne de commande
    // ======================================================================
    public partial class DelivLifePlugin
    {
        /// <summary>Accueil de la borne de commande.</summary>
        public async void KioskHome(Player player, int bizId)
        {
            DelivRestaurant restaurant = await GetRestaurantByBiz(bizId);
            if (restaurant == null)
            {
                Notify(player, "Cette borne n'est reliée à aucun restaurant.", NotificationManager.Type.Error);
                return;
            }

            DelivCart cart = GetCart(player, restaurant, false);

            Panel panel = PanelHelper.Create($"{restaurant.Name} - Borne de commande", UIPanel.PanelType.Tab, player, () => KioskHome(player, bizId));

            if (!restaurant.IsOpen)
            {
                panel.AddTabLine(Color("Le restaurant est actuellement fermé", Colors.Error), _ => { });
            }
            else
            {
                panel.AddTabLine(Color("Commander", Colors.Success), _ => KioskStart(player, restaurant));
            }
            panel.AddTabLine($"Mon panier ({cart.Count} article(s) - {Money(cart.Total)})", _ => CartView(player, restaurant, false));
            panel.AddTabLine("Suivre mes commandes", _ => ClientTracking(player, bizId));
            panel.AddTabLine("Écran des commandes", _ => OrderBoard(player, restaurant));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Choix « sur place » ou « à emporter » puis la carte.</summary>
        private void KioskStart(Player player, DelivRestaurant restaurant)
        {
            DelivCart cart = GetCart(player, restaurant, false);
            if (cart.ModeChosen)
            {
                CategoryMenu(player, restaurant, false);
                return;
            }
            KioskMode(player, restaurant);
        }

        private void KioskMode(Player player, DelivRestaurant restaurant)
        {
            Panel panel = PanelHelper.Create($"{restaurant.Name} - Bienvenue !", UIPanel.PanelType.Text, player, () => KioskMode(player, restaurant));
            panel.TextLines.Add(Bold(Align(restaurant.Name, Aligns.Center)));
            panel.TextLines.Add(Align(string.IsNullOrWhiteSpace(restaurant.WelcomeMessage)
                ? "Bienvenue ! Composez votre commande sur la borne, payez et récupérez votre ticket."
                : restaurant.WelcomeMessage, Aligns.Center));
            panel.TextLines.Add("");
            panel.TextLines.Add(Align("Où souhaitez-vous manger ?", Aligns.Center));

            panel.NextButton("Sur place", () => SetModeAndOpen(player, restaurant, DelivOrder.ModeOnSite));
            panel.NextButton("À emporter", () => SetModeAndOpen(player, restaurant, DelivOrder.ModeTakeAway));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void SetModeAndOpen(Player player, DelivRestaurant restaurant, int mode)
        {
            DelivCart cart = GetCart(player, restaurant, false);
            cart.Mode = mode;
            cart.ModeChosen = true;
            CategoryMenu(player, restaurant, false);
        }

        /// <summary>Liste des catégories de la carte (borne ou caisse).</summary>
        public async void CategoryMenu(Player player, DelivRestaurant restaurant, bool register)
        {
            List<DelivProduct> products = await GetProducts(restaurant.BizId, true);
            DelivCart cart = GetCart(player, restaurant, register);

            string title = register ? $"{restaurant.Name} - Caisse" : $"{restaurant.Name} - Notre carte";
            Panel panel = PanelHelper.Create(title, UIPanel.PanelType.TabPrice, player, () => CategoryMenu(player, restaurant, register));

            bool any = false;
            foreach (int category in DelivCategory.All)
            {
                int count = products.Count(p => p.Category == category);
                if (count == 0) continue;
                any = true;
                int c = category;
                panel.AddTabLine(Bold(DelivCategory.Label(category)), $"{count} choix", DelivCategory.Icon(category),
                    _ => ProductMenu(player, restaurant, register, c));
            }
            if (!any)
            {
                panel.AddTabLine("La carte est vide pour le moment", "", IconUtils.Others.None.Id, _ => { });
            }

            panel.NextButton("Choisir", () => panel.SelectTab());
            panel.NextButton($"Panier ({cart.Count}) - {Money(cart.Total)}", () => CartView(player, restaurant, register));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Produits d'une catégorie : un clic ajoute 1 au panier.</summary>
        public async void ProductMenu(Player player, DelivRestaurant restaurant, bool register, int category)
        {
            List<DelivProduct> products = (await GetProducts(restaurant.BizId, true)).Where(p => p.Category == category).ToList();
            DelivCart cart = GetCart(player, restaurant, register);
            string action = "add";

            Panel panel = PanelHelper.Create($"{DelivCategory.Label(category)} - Panier : {Money(cart.Total)}", UIPanel.PanelType.TabPrice, player,
                () => ProductMenu(player, restaurant, register, category));

            if (products.Count == 0)
            {
                panel.AddTabLine("Aucun produit disponible", "", IconUtils.Others.None.Id, _ => { });
            }
            foreach (DelivProduct product in products)
            {
                string label = Bold(product.Name);
                int inCart = cart.Lines.Where(l => l.ProductId == product.Id).Sum(l => l.Quantity);
                if (inCart > 0) label += " " + Color($"(x{inCart})", Colors.Success);

                panel.AddTabLine(label, Money(product.Price), product.GetIcon(), _ =>
                {
                    if (product.IsFormula && product.GetSlots().Count > 0)
                    {
                        FormulaStep(player, restaurant, register, product, new List<DelivProduct>());
                        return;
                    }
                    if (action == "qty")
                    {
                        AskQuantity(player, restaurant, register, product);
                        return;
                    }
                    GetCart(player, restaurant, register).Add(product, 1);
                    Notify(player, $"{product.Name} ajouté au panier.", NotificationManager.Type.Success);
                    panel.Refresh();
                });
            }

            if (products.Count > 0)
            {
                panel.AddButton("Ajouter", _ => { action = "add"; panel.SelectTab(); });
                panel.AddButton("Quantité...", _ => { action = "qty"; panel.SelectTab(); });
            }
            panel.NextButton("Panier", () => CartView(player, restaurant, register));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AskQuantity(Player player, DelivRestaurant restaurant, bool register, DelivProduct product)
        {
            Panel panel = PanelHelper.Create($"Quantité : {product.Name}", UIPanel.PanelType.Input, player,
                () => AskQuantity(player, restaurant, register, product));
            panel.TextLines.Add($"Combien de « {product.Name} » ({Money(product.Price)} l'unité) ?");
            panel.SetInputPlaceholder("1 - 50");

            panel.PreviousButtonWithAction("Ajouter", () =>
            {
                if (!int.TryParse(panel.inputText, out int quantity) || quantity <= 0 || quantity > 50)
                {
                    Notify(player, "Quantité invalide (1 à 50).", NotificationManager.Type.Error);
                    return Task.FromResult(false);
                }
                GetCart(player, restaurant, register).Add(product, quantity);
                Notify(player, $"{quantity}x {product.Name} ajouté(s) au panier.", NotificationManager.Type.Success);
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Composition d'une formule, étape par étape (une catégorie par étape).</summary>
        private async void FormulaStep(Player player, DelivRestaurant restaurant, bool register, DelivProduct formula, List<DelivProduct> chosen)
        {
            List<int> slots = formula.GetSlots();
            if (chosen.Count >= slots.Count)
            {
                GetCart(player, restaurant, register).Add(formula, 1, chosen);
                Notify(player, $"{formula.Name} ajoutée au panier : {string.Join(" + ", chosen.Select(c => c.Name))}", NotificationManager.Type.Success);
                CategoryMenu(player, restaurant, register);
                return;
            }

            int category = slots[chosen.Count];
            List<DelivProduct> choices = (await GetProducts(restaurant.BizId, true)).Where(p => p.Category == category).ToList();

            Panel panel = PanelHelper.Create($"{formula.Name} - Étape {chosen.Count + 1}/{slots.Count} : {DelivCategory.Singular(category)}",
                UIPanel.PanelType.TabPrice, player, () => FormulaStep(player, restaurant, register, formula, chosen));

            if (choices.Count == 0)
            {
                panel.AddTabLine($"Aucun(e) {DelivCategory.Singular(category).ToLower()} disponible", "", IconUtils.Others.None.Id, _ => { });
            }
            foreach (DelivProduct choice in choices)
            {
                panel.AddTabLine(choice.Name, Color("inclus", Colors.Success), choice.GetIcon(), _ =>
                {
                    List<DelivProduct> next = new List<DelivProduct>(chosen) { choice };
                    FormulaStep(player, restaurant, register, formula, next);
                });
            }

            if (choices.Count > 0) panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Panier : un clic retire une unité.</summary>
        public void CartView(Player player, DelivRestaurant restaurant, bool register)
        {
            DelivCart cart = GetCart(player, restaurant, register);

            Panel panel = PanelHelper.Create($"Panier - Total : {Money(cart.Total)}", UIPanel.PanelType.TabPrice, player,
                () => CartView(player, restaurant, register));

            if (cart.IsEmpty)
            {
                panel.AddTabLine("Votre panier est vide", "", IconUtils.Others.None.Id, _ => { });
            }
            foreach (DelivOrderLine line in cart.Lines.ToList())
            {
                string label = $"{line.Quantity}x {Bold(line.Name)}";
                if (!string.IsNullOrEmpty(line.Details)) label += Color($" ({Truncate(line.Details, 35)})", Colors.Grey);
                panel.AddTabLine(label, Money(line.Total), IconUtils.Others.None.Id, _ =>
                {
                    line.Quantity--;
                    if (line.Quantity <= 0) cart.Lines.Remove(line);
                    Notify(player, $"1x {line.Name} retiré du panier.");
                    panel.Refresh();
                });
            }

            if (!cart.IsEmpty)
            {
                panel.NextButton(Color("Commander", Colors.Success), () => OrderRecap(player, restaurant, register));
                panel.AddButton("Retirer 1", _ => panel.SelectTab());
                panel.AddButton(Color("Vider", Colors.Error), _ =>
                {
                    cart.Lines.Clear();
                    cart.Note = null;
                    panel.Refresh();
                });
            }
            panel.NextButton("Continuer", () => CategoryMenu(player, restaurant, register));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void AddRecapLines(Panel panel, DelivRestaurant restaurant, DelivCart cart)
        {
            panel.TextLines.Add(Bold(Align(restaurant.Name, Aligns.Center)));
            panel.TextLines.Add(Align(Color("Récapitulatif de la commande", Colors.Info), Aligns.Center));
            panel.TextLines.Add("");
            foreach (DelivOrderLine line in cart.Lines)
            {
                panel.TextLines.Add($"{line.Quantity}x {Bold(line.Name)}  —  {Money(line.Total)}");
                if (!string.IsNullOrEmpty(line.Details)) panel.TextLines.Add(Color($"   > {line.Details}", Colors.Grey));
            }
            panel.TextLines.Add("");
            panel.TextLines.Add($"Mode : {Bold(cart.Mode == DelivOrder.ModeTakeAway ? "À emporter" : "Sur place")}");
            if (!string.IsNullOrWhiteSpace(cart.Note)) panel.TextLines.Add($"Remarque : {Italic(cart.Note)}");
            panel.TextLines.Add(Bold($"TOTAL : {Money(cart.Total)}"));
        }

        /// <summary>Récapitulatif avant paiement.</summary>
        public void OrderRecap(Player player, DelivRestaurant restaurant, bool register)
        {
            DelivCart cart = GetCart(player, restaurant, register);
            if (cart.IsEmpty)
            {
                Notify(player, "Le panier est vide.", NotificationManager.Type.Warning);
                return;
            }

            Panel panel = PanelHelper.Create("Récapitulatif", UIPanel.PanelType.Text, player, () => OrderRecap(player, restaurant, register));
            AddRecapLines(panel, restaurant, cart);

            if (register)
            {
                panel.NextButton(Color("Encaisser", Colors.Success), () => RegisterChooseClient(player, restaurant));
            }
            else
            {
                if (restaurant.AcceptCash)
                    panel.AddButton(Color("Payer en espèces", Colors.Success), async _ => await KioskPay(player, restaurant, DelivOrder.PayCash, panel));
                if (restaurant.AcceptCard)
                    panel.AddButton(Color("Payer par carte", Colors.Info), async _ => await KioskPay(player, restaurant, DelivOrder.PayCard, panel));
                if (!restaurant.AcceptCash && !restaurant.AcceptCard)
                    panel.TextLines.Add(Color("Aucun moyen de paiement n'est activé, adressez-vous au comptoir.", Colors.Error));
            }

            panel.AddButton(cart.Mode == DelivOrder.ModeTakeAway ? "Passer « sur place »" : "Passer « à emporter »", _ =>
            {
                cart.Mode = cart.Mode == DelivOrder.ModeTakeAway ? DelivOrder.ModeOnSite : DelivOrder.ModeTakeAway;
                cart.ModeChosen = true;
                panel.Refresh();
            });
            panel.NextButton("Remarque", () => AskNote(player, restaurant, register));
            panel.PreviousButton("Modifier");
            panel.CloseButton();
            panel.Display();
        }

        private void AskNote(Player player, DelivRestaurant restaurant, bool register)
        {
            DelivCart cart = GetCart(player, restaurant, register);
            Panel panel = PanelHelper.Create("Remarque pour la cuisine", UIPanel.PanelType.Input, player, () => AskNote(player, restaurant, register));
            panel.TextLines.Add("Une précision pour la cuisine ? (ex. sans oignons, sauce à part...)");
            panel.SetInputPlaceholder(string.IsNullOrWhiteSpace(cart.Note) ? "Sans oignons" : cart.Note);
            panel.PreviousButtonWithAction("Valider", () =>
            {
                string note = panel.inputText?.Trim();
                cart.Note = string.IsNullOrEmpty(note) ? null : Truncate(note, 120);
                return Task.FromResult(true);
            });
            panel.PreviousButtonWithAction("Effacer", () =>
            {
                cart.Note = null;
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.Display();
        }

        private async Task KioskPay(Player player, DelivRestaurant restaurant, int payment, Panel panel)
        {
            DelivRestaurant fresh = await DelivRestaurant.Query(restaurant.Id);
            if (fresh == null || !fresh.IsOpen)
            {
                Notify(player, "Le restaurant est fermé.", NotificationManager.Type.Error);
                return;
            }
            if (fresh.RequireStaffOnDuty && !HasStaffOnDuty(fresh.BizId))
            {
                Notify(player, "Aucun employé n'est en service, revenez plus tard.", NotificationManager.Type.Warning);
                return;
            }

            DelivCart cart = GetCart(player, fresh, false);
            DelivOrder order = await PlaceOrder(player, fresh, cart, payment, DelivOrder.SourceKiosk, null);
            if (order == null) return;

            ClearCart(player, false);
            panel.Close();
            ShowTicket(player, fresh, order);
        }

        /// <summary>Ticket remis au client après paiement.</summary>
        public void ShowTicket(Player player, DelivRestaurant restaurant, DelivOrder order)
        {
            Panel panel = PanelHelper.Create($"Ticket n°{order.TicketLabel}", UIPanel.PanelType.Text, player, () => ShowTicket(player, restaurant, order));
            panel.TextLines.Add(Bold(Align(restaurant.Name, Aligns.Center)));
            panel.TextLines.Add(Align("Merci pour votre commande !", Aligns.Center));
            panel.TextLines.Add(Align(Bold(Color($"N° {order.TicketLabel}", Colors.Orange)), Aligns.Center));
            panel.TextLines.Add(Align(Color("Conservez ce numéro, il s'affichera à l'écran quand votre commande sera prête.", Colors.Grey), Aligns.Center));
            panel.TextLines.Add("");
            foreach (DelivOrderLine line in order.GetLines())
            {
                panel.TextLines.Add($"{line.Quantity}x {line.Name}  —  {Money(line.Total)}");
                if (!string.IsNullOrEmpty(line.Details)) panel.TextLines.Add(Color($"   > {line.Details}", Colors.Grey));
            }
            panel.TextLines.Add("");
            panel.TextLines.Add($"{order.ModeLabel} • Payé par {order.PaymentLabel.ToLower()} • {Hour(order.CreatedAt)}");
            panel.TextLines.Add(Bold($"TOTAL PAYÉ : {Money(order.Total)}"));
            panel.TextLines.Add($"Statut : {DelivStatus.Colored(order.Status)}");

            panel.NextButton("Suivre ma commande", () => ClientTracking(player, restaurant.BizId));
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Suivi des commandes du joueur (bizId = 0 : tous les restaurants).</summary>
        public async void ClientTracking(Player player, int bizId)
        {
            int characterId = player.character.Id;
            long since = Now() - 86400;
            List<DelivOrder> orders = (await DelivOrder.Query(o => o.ClientCharacterId == characterId && o.CreatedAt >= since))
                .Where(o => bizId == 0 || o.BizId == bizId)
                .OrderByDescending(o => o.CreatedAt)
                .Take(15)
                .ToList();
            Dictionary<int, string> names = (await DelivRestaurant.QueryAll()).ToDictionary(r => r.BizId, r => r.Name);

            Panel panel = PanelHelper.Create("Mes commandes", UIPanel.PanelType.TabPrice, player, () => ClientTracking(player, bizId));

            if (orders.Count == 0)
            {
                panel.AddTabLine("Aucune commande ces dernières 24 h", "", IconUtils.Others.None.Id, _ => { });
            }
            foreach (DelivOrder order in orders)
            {
                string restaurantName = names.TryGetValue(order.BizId, out string n) ? n : "?";
                panel.AddTabLine($"N° {Bold(order.TicketLabel)} • {DelivStatus.Colored(order.Status)} • {Truncate(restaurantName, 18)} • {Hour(order.CreatedAt)}",
                    Money(order.Total), IconUtils.Others.None.Id, _ => ClientOrderDetail(player, order.Id));
            }

            if (orders.Count > 0) panel.NextButton("Détails", () => panel.SelectTab());
            panel.AddButton("Actualiser", _ => panel.Refresh());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void ClientOrderDetail(Player player, int orderId)
        {
            DelivOrder order = await DelivOrder.Query(orderId);
            if (order == null) return;
            DelivRestaurant restaurant = await GetRestaurantByBiz(order.BizId);

            Panel panel = PanelHelper.Create($"Commande n°{order.TicketLabel}", UIPanel.PanelType.Text, player, () => ClientOrderDetail(player, orderId));
            panel.TextLines.Add(Bold(Align(restaurant?.Name ?? "Restaurant", Aligns.Center)));
            panel.TextLines.Add(Align(Bold($"N° {order.TicketLabel}"), Aligns.Center));
            panel.TextLines.Add(Align($"Statut : {DelivStatus.Colored(order.Status)}", Aligns.Center));
            panel.TextLines.Add(Align(StatusProgress(order.Status), Aligns.Center));
            panel.TextLines.Add("");
            foreach (DelivOrderLine line in order.GetLines())
            {
                panel.TextLines.Add($"{line.Quantity}x {line.Name}  —  {Money(line.Total)}");
                if (!string.IsNullOrEmpty(line.Details)) panel.TextLines.Add(Color($"   > {line.Details}", Colors.Grey));
            }
            if (!string.IsNullOrWhiteSpace(order.Note)) panel.TextLines.Add($"Remarque : {Italic(order.Note)}");
            panel.TextLines.Add($"{order.ModeLabel} • {order.PaymentLabel} • commandé {Elapsed(order.CreatedAt)}");
            panel.TextLines.Add(Bold($"TOTAL : {Money(order.Total)}"));

            if (order.Status == DelivStatus.EnAttente)
            {
                panel.AddButton(Color("Annuler (remboursé)", Colors.Error), async _ =>
                {
                    DelivOrder fresh = await DelivOrder.Query(orderId);
                    if (fresh == null || fresh.Status != DelivStatus.EnAttente)
                    {
                        Notify(player, "La commande est déjà en préparation, elle ne peut plus être annulée.", NotificationManager.Type.Warning);
                        panel.Refresh();
                        return;
                    }
                    if (await CancelOrder(fresh, player, true, "annulée par le client"))
                        panel.Refresh();
                });
            }
            panel.AddButton("Actualiser", _ => panel.Refresh());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static string StatusProgress(int status)
        {
            if (status == DelivStatus.Annulee) return Color("✖ Commande annulée", Colors.Error);
            string[] steps = { "Reçue", "En préparation", "Prête", "Remise" };
            int current = status == DelivStatus.EnAttente ? 0 : status == DelivStatus.EnPreparation ? 1 : status == DelivStatus.Prete ? 2 : 3;
            return string.Join(Color("  >  ", Colors.Grey), steps.Select((s, i) => i <= current ? Color(Bold(s), Colors.Success) : Color(s, Colors.Grey)));
        }

        /// <summary>Écran des commandes, comme au-dessus du comptoir d'un fast-food.</summary>
        public async void OrderBoard(Player player, DelivRestaurant restaurant)
        {
            int bizId = restaurant.BizId;
            List<DelivOrder> active = (await DelivOrder.Query(o => o.BizId == bizId && o.Status <= DelivStatus.Prete))
                .OrderBy(o => o.CreatedAt).ToList();

            string Numbers(IEnumerable<DelivOrder> list)
            {
                List<string> numbers = list.Select(o => o.TicketLabel).ToList();
                return numbers.Count == 0 ? Color("-", Colors.Grey) : string.Join("   ", numbers);
            }

            Panel panel = PanelHelper.Create($"{restaurant.Name} - Écran des commandes", UIPanel.PanelType.Text, player, () => OrderBoard(player, restaurant));
            panel.TextLines.Add(Bold(Color("EN PRÉPARATION", Colors.Orange)));
            panel.TextLines.Add(Bold(Numbers(active.Where(o => o.Status != DelivStatus.Prete))));
            panel.TextLines.Add("");
            panel.TextLines.Add(Bold(Color("PRÊTES - VENEZ AU COMPTOIR", Colors.Success)));
            panel.TextLines.Add(Bold(Numbers(active.Where(o => o.Status == DelivStatus.Prete))));

            panel.AddButton("Actualiser", _ => panel.Refresh());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }
}

namespace DelivLife
{
    // ======================================================================
    //  POINTS NATIFS : ouverture selon la fonction du point
    // ======================================================================
    public partial class DelivLifePlugin
    {
        public void OpenClientPoint(Player player, int bizId, int function)
        {
            switch (function)
            {
                case DelivKiosk.Board: OpenBoardPoint(player, bizId); break;
                case DelivKiosk.Pickup: PickupPoint(player, bizId); break;
                case DelivKiosk.Tracking: ClientTracking(player, bizId); break;
                default: KioskHome(player, bizId); break;
            }
        }

        public async void OpenEmployeePoint(Player player, int bizId, int function)
        {
            DelivRestaurant restaurant = await GetRestaurantByBiz(bizId);
            if (restaurant == null)
            {
                Notify(player, "Ce point n'est relié à aucun restaurant.", NotificationManager.Type.Error);
                return;
            }

            switch (function)
            {
                case DelivCounter.Register:
                    CategoryMenu(player, restaurant, true);
                    break;
                case DelivCounter.Kitchen:
                    OrdersList(player, bizId);
                    break;
                case DelivCounter.Duty:
                    ToggleDuty(player, restaurant);
                    break;
                case DelivCounter.Menu:
                    if (await IsManager(player, restaurant)) MenuManager(player, bizId);
                    else Notify(player, "Réservé au patron et aux gérants.", NotificationManager.Type.Warning);
                    break;
                case DelivCounter.Stats:
                    OfficePoint(player, bizId);
                    break;
                default:
                    EmployeeHome(player, bizId);
                    break;
            }
        }

        private void ToggleDuty(Player player, DelivRestaurant restaurant)
        {
            int id = player.character.Id;
            if (onDuty.Remove(id))
            {
                Notify(player, $"Fin de service chez {restaurant.Name}.", NotificationManager.Type.Info);
                return;
            }
            onDuty.Add(id);
            Notify(player, $"Prise de service chez {restaurant.Name} : vous recevrez les nouvelles commandes.", NotificationManager.Type.Success);
        }

        private async void OpenBoardPoint(Player player, int bizId)
        {
            DelivRestaurant restaurant = await GetRestaurantByBiz(bizId);
            if (restaurant != null) OrderBoard(player, restaurant);
        }

        /// <summary>Bureau employé : historique et statistiques.</summary>
        private void OfficePoint(Player player, int bizId)
        {
            Panel panel = PanelHelper.Create("DelivLife - Bureau", UIPanel.PanelType.Tab, player, () => OfficePoint(player, bizId));
            panel.AddTabLine("Historique du jour", _ => OrdersHistory(player, bizId));
            panel.AddTabLine("Statistiques", _ => Statistics(player, bizId));
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Point de retrait : le client signale qu'il attend sa commande prête au comptoir.</summary>
        public async void PickupPoint(Player player, int bizId)
        {
            DelivRestaurant restaurant = await GetRestaurantByBiz(bizId);
            if (restaurant == null) return;
            int characterId = player.character.Id;
            List<DelivOrder> orders = (await DelivOrder.Query(o => o.BizId == bizId && o.ClientCharacterId == characterId && o.Status <= DelivStatus.Prete))
                .OrderBy(o => o.CreatedAt).ToList();

            Panel panel = PanelHelper.Create($"{restaurant.Name} - Retrait des commandes", UIPanel.PanelType.TabPrice, player, () => PickupPoint(player, bizId));
            if (orders.Count == 0)
            {
                panel.AddTabLine("Vous n'avez aucune commande en cours ici", "", IconUtils.Others.None.Id, _ => { });
            }
            foreach (DelivOrder order in orders)
            {
                panel.AddTabLine($"N° {Bold(order.TicketLabel)} • {DelivStatus.Colored(order.Status)} • {order.ItemCount} art.",
                    Money(order.Total), IconUtils.Others.None.Id, _ =>
                    {
                        if (order.Status == DelivStatus.Prete)
                        {
                            NotifyEmployees(bizId, $"{player.FullName} attend sa commande n°{order.TicketLabel} au point de retrait.", NotificationManager.Type.Warning);
                            Notify(player, $"Le personnel est prévenu, votre commande n°{order.TicketLabel} va vous être remise.", NotificationManager.Type.Success);
                        }
                        else
                        {
                            Notify(player, $"Votre commande n°{order.TicketLabel} n'est pas encore prête ({order.StatusLabel.ToLower()}).", NotificationManager.Type.Info);
                        }
                    });
            }
            if (orders.Count > 0) panel.AddButton("Je suis là", _ => panel.SelectTab());
            panel.AddButton("Actualiser", _ => panel.Refresh());
            panel.NextButton("Écran des commandes", () => OrderBoard(player, restaurant));
            panel.CloseButton();
            panel.Display();
        }
    }
}

namespace DelivLife
{
    // ======================================================================
    //  COMMANDES : création, paiement, statut, remise, annulation
    // ======================================================================
    public partial class DelivLifePlugin
    {
        /// <summary>Débite le client, crédite l'entreprise, génère le ticket et prévient les employés.</summary>
        private async Task<DelivOrder> PlaceOrder(Player client, DelivRestaurant restaurant, DelivCart cart, int payment, int source, Player employee)
        {
            if (cart == null || cart.IsEmpty)
            {
                Notify(client, "Le panier est vide.", NotificationManager.Type.Error);
                return null;
            }
            if (payment == DelivOrder.PayCash && !restaurant.AcceptCash || payment == DelivOrder.PayCard && !restaurant.AcceptCard)
            {
                Notify(client, "Ce moyen de paiement n'est pas accepté ici.", NotificationManager.Type.Error);
                return null;
            }

            // Vérifie que tous les produits existent encore et sont disponibles
            Dictionary<int, DelivProduct> products = (await GetProducts(restaurant.BizId, false)).ToDictionary(p => p.Id);
            foreach (DelivOrderLine line in cart.Lines)
            {
                foreach (int id in line.ComponentIds.DefaultIfEmpty(line.ProductId))
                {
                    if (!products.TryGetValue(id, out DelivProduct p) || !p.IsAvailable)
                    {
                        Notify(client, $"« {line.Name} » n'est plus disponible, retirez-le du panier.", NotificationManager.Type.Error);
                        return null;
                    }
                }
            }

            double total = cart.Total;
            Bizs biz = Nova.biz.FetchBiz(restaurant.BizId);
            if (biz == null)
            {
                Notify(client, "Entreprise introuvable.", NotificationManager.Type.Error);
                return null;
            }

            if (payment == DelivOrder.PayCash)
            {
                if (client.Money < total)
                {
                    Notify(client, $"Vous n'avez pas assez d'espèces ({Money(total)} requis).", NotificationManager.Type.Error);
                    return null;
                }
                client.AddMoney(-total, $"{Tag} - {restaurant.Name}");
            }
            else
            {
                if (client.Bank < total)
                {
                    Notify(client, $"Paiement refusé : solde bancaire insuffisant ({Money(total)} requis).", NotificationManager.Type.Error);
                    return null;
                }
                client.AddBankMoney(-total, $"{Tag} - {restaurant.Name}");
            }

            biz.Bank += total;
            biz.Save();

            // Numéro de ticket : repart à 1 chaque jour, de 001 à 999
            int today = Today();
            if (restaurant.TicketDate != today)
            {
                restaurant.TicketDate = today;
                restaurant.TicketCounter = 0;
            }
            restaurant.TicketCounter = restaurant.TicketCounter >= 999 ? 1 : restaurant.TicketCounter + 1;
            await restaurant.Save();

            DelivOrder order = new DelivOrder
            {
                BizId = restaurant.BizId,
                Ticket = restaurant.TicketCounter,
                TicketDate = today,
                ClientCharacterId = client.character.Id,
                ClientName = client.FullName,
                Total = total,
                PaymentMethod = payment,
                Status = DelivStatus.EnAttente,
                Source = source,
                Mode = cart.Mode,
                Note = cart.Note,
                EmployeeName = employee?.FullName,
                CreatedAt = Now(),
                UpdatedAt = Now()
            };
            order.SetLines(cart.Lines);

            if (!await order.Save())
            {
                Logger.LogError(Tag, $"Commande non enregistrée pour {client.FullName} ({Money(total)}), remboursement.");
                if (payment == DelivOrder.PayCash) client.AddMoney(total, $"{Tag} - remboursement");
                else client.AddBankMoney(total, $"{Tag} - remboursement");
                biz.Bank -= total;
                biz.Save();
                Notify(client, "Erreur lors de l'enregistrement de la commande, vous avez été remboursé.", NotificationManager.Type.Error);
                return null;
            }

            Notify(client, $"Paiement accepté ({Money(total)}). Votre numéro : {order.TicketLabel}", NotificationManager.Type.Success);
            client.SendText($"<color=#f5a742>[{restaurant.Name}]</color> Commande n°{order.TicketLabel} payée ({Money(total)}). Suivi : /commande");
            NotifyEmployees(restaurant.BizId, $"Nouvelle commande n°{order.TicketLabel} ({order.ItemCount} article(s), {Money(total)}, {order.ModeLabel.ToLower()})",
                NotificationManager.Type.Warning);
            SendOrderWebhook(restaurant, order, "#f5a742", "Nouvelle commande");
            return order;
        }

        /// <summary>Change le statut d'une commande et prévient le client.</summary>
        private async Task<bool> SetStatus(Player employee, DelivOrder order, int status)
        {
            order.Status = status;
            order.UpdatedAt = Now();
            if (employee != null && (status == DelivStatus.EnPreparation || string.IsNullOrEmpty(order.EmployeeName)))
                order.EmployeeName = employee.FullName;
            if (!await order.Save())
            {
                Notify(employee, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                return false;
            }

            DelivRestaurant restaurant = await GetRestaurantByBiz(order.BizId);
            string name = restaurant?.Name ?? Tag;
            Player client = GetOnlineCharacter(order.ClientCharacterId);

            switch (status)
            {
                case DelivStatus.EnPreparation:
                    client?.Notify(name, $"Votre commande n°{order.TicketLabel} est en préparation.", NotificationManager.Type.Info, 6f);
                    break;
                case DelivStatus.Prete:
                    client?.Notify(name, $"Votre commande n°{order.TicketLabel} est PRÊTE ! Venez la récupérer au comptoir.", NotificationManager.Type.Success, 10f);
                    client?.SendText($"<color=#85df6a>[{name}]</color> Commande n°{order.TicketLabel} prête, venez la récupérer au comptoir !");
                    SendOrderWebhook(restaurant, order, "#85df6a", "Commande prête");
                    break;
                case DelivStatus.Remise:
                    client?.Notify(name, $"Commande n°{order.TicketLabel} remise. Bon appétit !", NotificationManager.Type.Success, 6f);
                    SendOrderWebhook(restaurant, order, "#adadad", "Commande remise");
                    break;
            }
            return true;
        }

        /// <summary>Remet la commande au client (objets du jeu éventuels) et la clôture.</summary>
        private async Task<bool> HandOver(Player employee, DelivOrder order)
        {
            DelivRestaurant restaurant = await GetRestaurantByBiz(order.BizId);
            if (restaurant == null) return false;

            Dictionary<int, int> items = new Dictionary<int, int>();
            if (restaurant.GiveItems)
            {
                foreach (DelivOrderLine line in order.GetLines())
                    foreach (int itemId in line.ItemIds)
                        if (ItemUtils.GetItemById(itemId) != null)
                            items[itemId] = (items.TryGetValue(itemId, out int q) ? q : 0) + line.Quantity;
            }

            if (items.Count > 0)
            {
                Player client = GetOnlineCharacter(order.ClientCharacterId);
                if (client == null)
                {
                    Notify(employee, "Le client n'est pas connecté : impossible de lui remettre les produits.", NotificationManager.Type.Error);
                    return false;
                }

                if (restaurant.TakeItemsFromEmployee)
                {
                    foreach (KeyValuePair<int, int> item in items)
                    {
                        if (!InventoryUtils.CheckInventoryContainsItem(employee, item.Key, item.Value))
                        {
                            Notify(employee, $"Il vous manque {item.Value}x {ItemName(employee, item.Key)} dans votre inventaire.", NotificationManager.Type.Error);
                            return false;
                        }
                    }
                    foreach (KeyValuePair<int, int> item in items)
                        InventoryUtils.RemoveFromInventory(employee, item.Key, item.Value);
                }

                foreach (KeyValuePair<int, int> item in items)
                {
                    if (!InventoryUtils.AddItem(client, item.Key, item.Value))
                    {
                        Notify(employee, $"Inventaire du client plein : {item.Value}x {ItemName(employee, item.Key)} non remis.", NotificationManager.Type.Warning);
                        Notify(client, $"Inventaire plein : {item.Value}x {ItemName(client, item.Key)} n'a pas pu vous être remis.", NotificationManager.Type.Warning);
                        if (restaurant.TakeItemsFromEmployee) InventoryUtils.AddItem(employee, item.Key, item.Value);
                    }
                }
            }

            return await SetStatus(employee, order, DelivStatus.Remise);
        }

        /// <summary>Annule une commande. Si refund, rembourse le client depuis le compte de l'entreprise.</summary>
        private async Task<bool> CancelOrder(DelivOrder order, Player by, bool refund, string reason)
        {
            if (!order.IsActive)
            {
                Notify(by, "Cette commande est déjà clôturée.", NotificationManager.Type.Warning);
                return false;
            }

            Player client = GetOnlineCharacter(order.ClientCharacterId);
            if (refund)
            {
                if (client == null)
                {
                    Notify(by, "Le client doit être connecté pour être remboursé.", NotificationManager.Type.Error);
                    return false;
                }
                Bizs biz = Nova.biz.FetchBiz(order.BizId);
                if (biz == null || biz.Bank < order.Total)
                {
                    Notify(by, "Le compte de l'entreprise ne permet pas le remboursement.", NotificationManager.Type.Error);
                    return false;
                }
                biz.Bank -= order.Total;
                biz.Save();
                if (order.PaymentMethod == DelivOrder.PayCash) client.AddMoney(order.Total, $"{Tag} - remboursement");
                else client.AddBankMoney(order.Total, $"{Tag} - remboursement");
            }

            order.Status = DelivStatus.Annulee;
            order.UpdatedAt = Now();
            order.Note = string.IsNullOrWhiteSpace(order.Note) ? $"[{reason}]" : $"{order.Note} [{reason}]";
            await order.Save();

            DelivRestaurant restaurant = await GetRestaurantByBiz(order.BizId);
            string refundText = refund ? $" Vous avez été remboursé de {Money(order.Total)}." : "";
            client?.Notify(restaurant?.Name ?? Tag, $"Commande n°{order.TicketLabel} annulée.{refundText}", NotificationManager.Type.Warning, 8f);
            if (by != null && by != client) Notify(by, $"Commande n°{order.TicketLabel} annulée.{(refund ? " Client remboursé." : "")}", NotificationManager.Type.Success);
            NotifyEmployees(order.BizId, $"Commande n°{order.TicketLabel} annulée ({reason}).", NotificationManager.Type.Error);
            SendOrderWebhook(restaurant, order, "#dd304e", "Commande annulée");
            return true;
        }

        private static string ItemName(Player player, int itemId)
        {
            Item item = ItemUtils.GetItemById(itemId);
            return item != null ? player.NewTranslate("Items", item.itemName) : $"Objet #{itemId}";
        }
    }
}

namespace DelivLife
{
    // ======================================================================
    //  CÔTÉ EMPLOYÉS : menu Métier (AAMenu), commandes, caisse, statistiques
    // ======================================================================
    public partial class DelivLifePlugin
    {
        /// <summary>Point d'entrée employé : vérifie que l'entreprise du joueur a accès à DelivLife.</summary>
        public async void EmployeeEntry(Player player)
        {
            if (player == null) return;
            if (!player.HasBiz || player.biz == null)
            {
                Notify(player, "Vous devez faire partie d'une entreprise.", NotificationManager.Type.Error);
                return;
            }
            DelivRestaurant restaurant = await GetRestaurantByBiz(player.biz.Id);
            if (restaurant == null)
            {
                Notify(player, "Votre entreprise n'a pas accès à DelivLife (à activer par le staff).", NotificationManager.Type.Error);
                return;
            }
            EmployeeHome(player, restaurant.BizId);
        }

        public async void EmployeeHome(Player player, int bizId)
        {
            DelivRestaurant restaurant = await GetRestaurantByBiz(bizId);
            if (restaurant == null) return;

            int active = (await DelivOrder.Query(o => o.BizId == bizId && o.Status <= DelivStatus.Prete)).Count;
            bool manager = await IsManager(player, restaurant);
            bool duty = onDuty.Contains(player.character.Id);

            Panel panel = PanelHelper.Create($"DelivLife - {restaurant.Name}", UIPanel.PanelType.Tab, player, () => EmployeeHome(player, bizId));

            panel.AddTabLine($"Service : {(duty ? Color("EN SERVICE", Colors.Success) : Color("hors service", Colors.Grey))}", _ =>
            {
                if (onDuty.Contains(player.character.Id)) onDuty.Remove(player.character.Id);
                else onDuty.Add(player.character.Id);
                Notify(player, onDuty.Contains(player.character.Id)
                    ? "Vous êtes en service : vous recevrez les nouvelles commandes."
                    : "Vous n'êtes plus en service.", NotificationManager.Type.Info);
                panel.Refresh();
            });
            panel.AddTabLine($"Commandes en cours {Color($"({active})", active > 0 ? Colors.Warning : Colors.Grey)}", _ => OrdersList(player, bizId));
            panel.AddTabLine("Caisse enregistreuse", _ => CategoryMenu(player, restaurant, true));
            panel.AddTabLine("Écran des commandes", _ => OrderBoard(player, restaurant));
            panel.AddTabLine("Historique du jour", _ => OrdersHistory(player, bizId));
            panel.AddTabLine("Statistiques", _ => Statistics(player, bizId));
            if (manager)
            {
                panel.AddTabLine(Color("Gestion de la carte", Colors.Info), _ => MenuManager(player, bizId));
                panel.AddTabLine(Color("Paramètres du restaurant", Colors.Info), _ => RestaurantSettings(player, bizId));
            }

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static string OrderLineLabel(DelivOrder order)
        {
            return $"N° {Bold(order.TicketLabel)} • {DelivStatus.Colored(order.Status)} • {Truncate(order.ClientName ?? "-", 18)} • {Hour(order.CreatedAt)}";
        }

        /// <summary>Commandes à traiter, les plus anciennes d'abord.</summary>
        public async void OrdersList(Player player, int bizId)
        {
            List<DelivOrder> orders = (await DelivOrder.Query(o => o.BizId == bizId && o.Status <= DelivStatus.Prete))
                .OrderBy(o => o.CreatedAt).ToList();

            Panel panel = PanelHelper.Create($"Commandes en cours ({orders.Count})", UIPanel.PanelType.TabPrice, player, () => OrdersList(player, bizId));

            if (orders.Count == 0)
            {
                panel.AddTabLine("Aucune commande en attente", "", IconUtils.Others.None.Id, _ => { });
            }
            foreach (DelivOrder order in orders)
            {
                panel.AddTabLine(OrderLineLabel(order), Money(order.Total), IconUtils.Others.None.Id, _ => OrderDetail(player, order.Id));
            }

            if (orders.Count > 0) panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.AddButton("Actualiser", _ => panel.Refresh());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Détail d'une commande côté employé, avec les actions de suivi.</summary>
        public async void OrderDetail(Player player, int orderId)
        {
            DelivOrder order = await DelivOrder.Query(orderId);
            if (order == null) return;
            DelivRestaurant restaurant = await GetRestaurantByBiz(order.BizId);
            bool manager = restaurant != null && await IsManager(player, restaurant);

            Panel panel = PanelHelper.Create($"Commande n°{order.TicketLabel} - {order.StatusLabel}", UIPanel.PanelType.Text, player,
                () => OrderDetail(player, orderId));

            panel.TextLines.Add(Bold($"N° {order.TicketLabel}") + "   " + DelivStatus.Colored(order.Status));
            panel.TextLines.Add($"Client : {Bold(order.ClientName)}{(GetOnlineCharacter(order.ClientCharacterId) == null ? Color(" (hors ligne)", Colors.Grey) : "")}");
            panel.TextLines.Add($"{order.ModeLabel} • {order.SourceLabel} • {order.PaymentLabel} • {Hour(order.CreatedAt)} ({Elapsed(order.CreatedAt)})");
            if (!string.IsNullOrEmpty(order.EmployeeName)) panel.TextLines.Add($"Employé : {order.EmployeeName}");
            panel.TextLines.Add("");
            foreach (DelivOrderLine line in order.GetLines())
            {
                panel.TextLines.Add($"{Bold($"{line.Quantity}x")} {line.Name}  —  {Money(line.Total)}");
                if (!string.IsNullOrEmpty(line.Details)) panel.TextLines.Add(Color($"     ↳ {line.Details}", Colors.Info));
            }
            if (!string.IsNullOrWhiteSpace(order.Note)) panel.TextLines.Add(Color($"Remarque : {order.Note}", Colors.Warning));
            panel.TextLines.Add(Bold($"TOTAL : {Money(order.Total)}"));

            if (order.Status == DelivStatus.EnAttente)
                panel.AddButton(Color("Prendre en charge", Colors.Orange), async _ => { if (await SetStatus(player, order, DelivStatus.EnPreparation)) panel.Refresh(); });
            if (order.Status == DelivStatus.EnAttente || order.Status == DelivStatus.EnPreparation)
                panel.AddButton(Color("Commande prête", Colors.Success), async _ => { if (await SetStatus(player, order, DelivStatus.Prete)) panel.Refresh(); });
            if (order.IsActive)
                panel.AddButton(Color("Remettre au client", Colors.Info), async _ => { if (await HandOver(player, order)) panel.Previous(); });
            if (order.IsActive)
                panel.AddButton(Color("Annuler + rembourser", Colors.Error), async _ =>
                {
                    if (await CancelOrder(order, player, true, $"annulée par {player.FullName}")) panel.Previous();
                });
            if (order.IsActive && manager)
                panel.AddButton(Color("Annuler sans rembourser", Colors.Error), async _ =>
                {
                    if (await CancelOrder(order, player, false, $"annulée sans remboursement par {player.FullName}")) panel.Previous();
                });

            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Commandes du jour (toutes).</summary>
        public async void OrdersHistory(Player player, int bizId)
        {
            int today = Today();
            List<DelivOrder> orders = (await DelivOrder.Query(o => o.BizId == bizId && o.TicketDate == today))
                .OrderByDescending(o => o.CreatedAt).ToList();

            Panel panel = PanelHelper.Create($"Historique du jour ({orders.Count})", UIPanel.PanelType.TabPrice, player, () => OrdersHistory(player, bizId));
            if (orders.Count == 0)
            {
                panel.AddTabLine("Aucune commande aujourd'hui", "", IconUtils.Others.None.Id, _ => { });
            }
            foreach (DelivOrder order in orders)
            {
                panel.AddTabLine(OrderLineLabel(order), Money(order.Total), IconUtils.Others.None.Id, _ => OrderDetail(player, order.Id));
            }
            if (orders.Count > 0) panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async void Statistics(Player player, int bizId)
        {
            int today = Today();
            List<DelivOrder> all = await DelivOrder.Query(o => o.BizId == bizId);
            List<DelivOrder> paid = all.Where(o => o.Status != DelivStatus.Annulee).ToList();
            List<DelivOrder> todayPaid = paid.Where(o => o.TicketDate == today).ToList();
            long weekAgo = Now() - 7 * 86400;
            List<DelivOrder> weekPaid = paid.Where(o => o.CreatedAt >= weekAgo).ToList();

            Panel panel = PanelHelper.Create("Statistiques", UIPanel.PanelType.Text, player, () => Statistics(player, bizId));

            panel.TextLines.Add(Bold(Color("Aujourd'hui", Colors.Info)));
            panel.TextLines.Add($"Commandes : {todayPaid.Count} • Chiffre d'affaires : {Bold(Money(todayPaid.Sum(o => o.Total)))}");
            panel.TextLines.Add($"Espèces : {Money(todayPaid.Where(o => o.PaymentMethod == DelivOrder.PayCash).Sum(o => o.Total))} • " +
                                $"Carte : {Money(todayPaid.Where(o => o.PaymentMethod == DelivOrder.PayCard).Sum(o => o.Total))}");
            panel.TextLines.Add($"Borne : {todayPaid.Count(o => o.Source == DelivOrder.SourceKiosk)} • Caisse : {todayPaid.Count(o => o.Source == DelivOrder.SourceRegister)} • " +
                                $"Panier moyen : {Money(todayPaid.Count > 0 ? todayPaid.Average(o => o.Total) : 0)}");
            panel.TextLines.Add($"Annulées : {all.Count(o => o.TicketDate == today && o.Status == DelivStatus.Annulee)}");
            panel.TextLines.Add("");
            panel.TextLines.Add(Bold(Color("7 derniers jours", Colors.Info)));
            panel.TextLines.Add($"Commandes : {weekPaid.Count} • Chiffre d'affaires : {Bold(Money(weekPaid.Sum(o => o.Total)))}");
            panel.TextLines.Add("");
            panel.TextLines.Add(Bold(Color("Top produits (7 jours)", Colors.Info)));
            var top = weekPaid.SelectMany(o => o.GetLines())
                .GroupBy(l => l.Name)
                .Select(g => new { Name = g.Key, Qty = g.Sum(l => l.Quantity), Total = g.Sum(l => l.Total) })
                .OrderByDescending(x => x.Qty)
                .Take(5)
                .ToList();
            if (top.Count == 0) panel.TextLines.Add(Color("Aucune vente", Colors.Grey));
            for (int i = 0; i < top.Count; i++)
                panel.TextLines.Add($"{i + 1}. {top[i].Name} — {top[i].Qty} vendu(s) • {Money(top[i].Total)}");
            panel.TextLines.Add("");
            panel.TextLines.Add(Color($"Depuis le début : {paid.Count} commandes • {Money(paid.Sum(o => o.Total))}", Colors.Grey));

            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Caisse enregistreuse : l'employé compose la commande puis envoie
        //  une demande de paiement à un client proche.
        // ------------------------------------------------------------------

        private void RegisterChooseClient(Player player, DelivRestaurant restaurant)
        {
            DelivCart cart = GetCart(player, restaurant, true);
            List<Player> nearby = Nova.server.GetPlayersInRange(6f, player.setup.transform.position)
                .Where(p => p != null && p.isInGame && p.character != null && p.character.Id != player.character.Id)
                .ToList();

            Panel panel = PanelHelper.Create($"Encaisser {Money(cart.Total)} - Choisir le client", UIPanel.PanelType.Tab, player,
                () => RegisterChooseClient(player, restaurant));

            if (nearby.Count == 0)
            {
                panel.AddTabLine("Aucun client à proximité (6 m)", _ => { });
            }
            foreach (Player client in nearby)
            {
                panel.AddTabLine(client.FullName, _ => SendPaymentRequest(player, client, restaurant));
            }

            if (nearby.Count > 0) panel.AddButton("Envoyer", _ => panel.SelectTab());
            panel.AddButton("Actualiser", _ => panel.Refresh());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void SendPaymentRequest(Player employee, Player client, DelivRestaurant restaurant)
        {
            DelivCart cart = GetCart(employee, restaurant, true);
            if (cart.IsEmpty) return;

            DelivPaymentRequest request = new DelivPaymentRequest
            {
                EmployeeCharacterId = employee.character.Id,
                EmployeeName = employee.FullName,
                RestaurantId = restaurant.Id,
                Cart = cart.Clone(),
                CreatedAt = Now()
            };
            paymentRequests[client.character.Id] = request;

            Notify(employee, $"Demande de paiement de {Money(cart.Total)} envoyée à {client.FullName}.", NotificationManager.Type.Info);
            ClientPaymentRequest(client, request, restaurant);
        }

        /// <summary>Panel reçu par le client : payer la commande saisie en caisse.</summary>
        private void ClientPaymentRequest(Player client, DelivPaymentRequest request, DelivRestaurant restaurant)
        {
            Panel panel = PanelHelper.Create("Demande de paiement", UIPanel.PanelType.Text, client, () => ClientPaymentRequest(client, request, restaurant));

            AddRecapLines(panel, restaurant, request.Cart);
            panel.TextLines.Add(Color($"Encaissé par {request.EmployeeName}", Colors.Grey));

            async Task Pay(int method)
            {
                if (request.Done || !paymentRequests.TryGetValue(client.character.Id, out DelivPaymentRequest current) || current != request)
                {
                    Notify(client, "Cette demande de paiement n'est plus valable.", NotificationManager.Type.Warning);
                    panel.Close();
                    return;
                }
                if (Now() - request.CreatedAt > 300)
                {
                    paymentRequests.Remove(client.character.Id);
                    Notify(client, "Demande de paiement expirée.", NotificationManager.Type.Warning);
                    panel.Close();
                    return;
                }

                DelivRestaurant fresh = await DelivRestaurant.Query(request.RestaurantId);
                Player employee = GetOnlineCharacter(request.EmployeeCharacterId);
                DelivOrder order = await PlaceOrder(client, fresh, request.Cart, method, DelivOrder.SourceRegister, employee);
                if (order == null) return;

                request.Done = true;
                paymentRequests.Remove(client.character.Id);
                if (employee != null)
                {
                    ClearCart(employee, true);
                    Notify(employee, $"{client.FullName} a payé {Money(order.Total)} ({order.PaymentLabel.ToLower()}). Ticket n°{order.TicketLabel}.", NotificationManager.Type.Success);
                }
                panel.Close();
                ShowTicket(client, fresh, order);
            }

            if (restaurant.AcceptCash) panel.AddButton(Color("Payer en espèces", Colors.Success), async _ => await Pay(DelivOrder.PayCash));
            if (restaurant.AcceptCard) panel.AddButton(Color("Payer par carte", Colors.Info), async _ => await Pay(DelivOrder.PayCard));
            panel.AddButton(Color("Refuser", Colors.Error), _ =>
            {
                paymentRequests.Remove(client.character.Id);
                GetOnlineCharacter(request.EmployeeCharacterId)?.Notify(Tag, $"{client.FullName} a refusé le paiement.", NotificationManager.Type.Warning, 6f);
                panel.Close();
            });
            panel.Display();
        }
    }
}

namespace DelivLife
{
    // ======================================================================
    //  GÉRANT : gestion de la carte et paramètres
    // ======================================================================
    public partial class DelivLifePlugin
    {
        public async void MenuManager(Player player, int bizId)
        {
            DelivRestaurant restaurant = await GetRestaurantByBiz(bizId);
            if (restaurant == null || !await IsManager(player, restaurant)) return;

            List<DelivProduct> products = await GetProducts(bizId, false);

            Panel panel = PanelHelper.Create($"Carte - {restaurant.Name} ({products.Count} produits)", UIPanel.PanelType.TabPrice, player,
                () => MenuManager(player, bizId));

            if (products.Count == 0)
            {
                panel.AddTabLine("Carte vide : ajoutez un produit", "", IconUtils.Others.None.Id, _ => { });
            }
            foreach (DelivProduct product in products)
            {
                string state = product.IsAvailable ? Color("disponible", Colors.Success) : Color("rupture", Colors.Error);
                string label = $"{Bold(product.Name)} • {DelivCategory.Singular(product.Category)} • {state}";
                panel.AddTabLine(label, Money(product.Price), product.GetIcon(), _ => ProductEditor(player, bizId, product.Id));
            }

            if (products.Count > 0) panel.NextButton("Modifier", () => panel.SelectTab());
            panel.NextButton(Color("Ajouter", Colors.Success), () => NewProductCategory(player, bizId));
            if (products.Count == 0)
            {
                panel.AddButton("Carte d'exemple", async _ =>
                {
                    await SeedExampleMenu(bizId);
                    Notify(player, "Carte d'exemple créée, modifiez-la comme vous voulez.", NotificationManager.Type.Success);
                    panel.Refresh();
                });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void NewProductCategory(Player player, int bizId)
        {
            Panel panel = PanelHelper.Create("Nouveau produit - Catégorie", UIPanel.PanelType.TabPrice, player, () => NewProductCategory(player, bizId));
            foreach (int category in DelivCategory.All)
            {
                int c = category;
                panel.AddTabLine(DelivCategory.Singular(category), "", DelivCategory.Icon(category), _ => NewProductName(player, bizId, c));
            }
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void NewProductName(Player player, int bizId, int category)
        {
            Panel panel = PanelHelper.Create($"Nouveau produit - {DelivCategory.Singular(category)}", UIPanel.PanelType.Input, player,
                () => NewProductName(player, bizId, category));
            panel.TextLines.Add("Nom du produit et prix, séparés par « ; »");
            panel.TextLines.Add(Color("Exemple : Burger Classique ; 8,50", Colors.Grey));
            panel.SetInputPlaceholder("Nom ; prix");

            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string[] parts = (panel.inputText ?? "").Split(';');
                string name = parts[0].Trim();
                if (string.IsNullOrEmpty(name) || parts.Length < 2 || !TryParsePrice(parts[1], out double price))
                {
                    Notify(player, "Format attendu : Nom ; prix (ex. Coca-Cola ; 2,50)", NotificationManager.Type.Error);
                    return false;
                }
                DelivProduct product = new DelivProduct
                {
                    BizId = bizId,
                    Name = Truncate(name, 40),
                    Category = category,
                    Price = price,
                    IsAvailable = true,
                    IconId = -1
                };
                if (category == DelivCategory.Formule)
                    product.SetSlots(new List<int> { DelivCategory.Plat, DelivCategory.Accompagnement, DelivCategory.Boisson });
                if (!await product.Save())
                {
                    Notify(player, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                    return false;
                }
                Notify(player, $"« {product.Name} » ajouté à la carte ({Money(price)}).", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Fiche produit : chaque ligne modifie un champ.</summary>
        public async void ProductEditor(Player player, int bizId, int productId)
        {
            DelivProduct product = await DelivProduct.Query(productId);
            if (product == null) return;

            Panel panel = PanelHelper.Create($"Produit : {product.Name}", UIPanel.PanelType.Tab, player, () => ProductEditor(player, bizId, productId));

            panel.AddTabLine($"{Color("Nom :", Colors.Info)} {product.Name}", _ =>
                EditProductText(player, product, "Nom", product.Name, (p, v) => p.Name = Truncate(v, 40)));
            panel.AddTabLine($"{Color("Prix :", Colors.Info)} {Money(product.Price)}", _ => EditProductPrice(player, product));
            panel.AddTabLine($"{Color("Catégorie :", Colors.Info)} {DelivCategory.Singular(product.Category)}", _ => EditProductCategory(player, product));
            panel.AddTabLine($"{Color("Description :", Colors.Info)} {(string.IsNullOrWhiteSpace(product.Description) ? Color("aucune", Colors.Grey) : product.Description)}", _ =>
                EditProductText(player, product, "Description (vide = aucune)", product.Description, (p, v) => p.Description = string.IsNullOrWhiteSpace(v) ? null : Truncate(v, 80)));
            panel.AddTabLine($"{Color("Disponible :", Colors.Info)} {(product.IsAvailable ? Color("Oui", Colors.Success) : Color("Non (rupture de stock)", Colors.Error))}", async _ =>
            {
                product.IsAvailable = !product.IsAvailable;
                await product.Save();
                panel.Refresh();
            });
            string itemLabel = product.ItemId > 0 ? $"{ItemName(player, product.ItemId)} (#{product.ItemId})" : Color("aucun", Colors.Grey);
            panel.AddTabLine($"{Color("Objet remis au client :", Colors.Info)} {itemLabel}", _ => EditProductItem(player, product));
            panel.AddTabLine($"{Color("Icône :", Colors.Info)} {(product.IconId >= 0 ? $"#{product.IconId}" : "automatique")}", _ => EditProductIcon(player, product));
            if (product.IsFormula)
            {
                string slots = product.GetSlots().Count == 0 ? Color("aucun choix (formule fixe)", Colors.Grey)
                    : string.Join(" + ", product.GetSlots().Select(DelivCategory.Singular));
                panel.AddTabLine($"{Color("Composition :", Colors.Info)} {slots}", _ => FormulaEditor(player, product.Id));
            }

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.AddButton(Color("Supprimer", Colors.Error), async _ =>
            {
                if (await product.Delete())
                {
                    Notify(player, $"« {product.Name} » supprimé de la carte.", NotificationManager.Type.Success);
                    panel.Previous();
                }
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EditProductText(Player player, DelivProduct product, string field, string current, Action<DelivProduct, string> apply)
        {
            Panel panel = PanelHelper.Create($"{product.Name} - {field}", UIPanel.PanelType.Input, player,
                () => EditProductText(player, product, field, current, apply));
            panel.TextLines.Add(field);
            panel.SetInputPlaceholder(current ?? "");
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string value = panel.inputText?.Trim();
                if (field == "Nom" && string.IsNullOrEmpty(value)) return false;
                apply(product, value);
                return await product.Save();
            });
            panel.PreviousButton();
            panel.Display();
        }

        private void EditProductPrice(Player player, DelivProduct product)
        {
            Panel panel = PanelHelper.Create($"{product.Name} - Prix", UIPanel.PanelType.Input, player, () => EditProductPrice(player, product));
            panel.TextLines.Add($"Nouveau prix (actuel : {Money(product.Price)})");
            panel.SetInputPlaceholder(product.Price.ToString("0.00", CultureInfo.InvariantCulture));
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!TryParsePrice(panel.inputText, out double price))
                {
                    Notify(player, "Prix invalide.", NotificationManager.Type.Error);
                    return false;
                }
                product.Price = price;
                return await product.Save();
            });
            panel.PreviousButton();
            panel.Display();
        }

        private void EditProductCategory(Player player, DelivProduct product)
        {
            Panel panel = PanelHelper.Create($"{product.Name} - Catégorie", UIPanel.PanelType.Tab, player, () => EditProductCategory(player, product));
            foreach (int category in DelivCategory.All)
            {
                int c = category;
                panel.AddTabLine(DelivCategory.Singular(category) + (c == product.Category ? Color(" (actuelle)", Colors.Grey) : ""), async _ =>
                {
                    product.Category = c;
                    if (c == DelivCategory.Formule && product.GetSlots().Count == 0)
                        product.SetSlots(new List<int> { DelivCategory.Plat, DelivCategory.Accompagnement, DelivCategory.Boisson });
                    if (await product.Save()) panel.Previous();
                });
            }
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.Display();
        }

        private void EditProductItem(Player player, DelivProduct product)
        {
            Panel panel = PanelHelper.Create($"{product.Name} - Objet", UIPanel.PanelType.Input, player, () => EditProductItem(player, product));
            panel.TextLines.Add("ID de l'objet du jeu remis au client lors de la remise de la commande.");
            panel.TextLines.Add(Color("0 = aucun objet (commande purement RP).", Colors.Grey));
            panel.SetInputPlaceholder(product.ItemId.ToString());
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!int.TryParse(panel.inputText, out int itemId) || itemId < 0)
                {
                    Notify(player, "ID invalide.", NotificationManager.Type.Error);
                    return false;
                }
                if (itemId > 0 && ItemUtils.GetItemById(itemId) == null)
                {
                    Notify(player, $"L'objet #{itemId} n'existe pas.", NotificationManager.Type.Error);
                    return false;
                }
                product.ItemId = itemId;
                return await product.Save();
            });
            panel.PreviousButton();
            panel.Display();
        }

        private void EditProductIcon(Player player, DelivProduct product)
        {
            Panel panel = PanelHelper.Create($"{product.Name} - Icône", UIPanel.PanelType.Input, player, () => EditProductIcon(player, product));
            panel.TextLines.Add("ID d'icône affichée sur la borne (-1 = automatique : icône de l'objet ou de la catégorie).");
            panel.SetInputPlaceholder(product.IconId.ToString());
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!int.TryParse(panel.inputText, out int iconId) || iconId < -1)
                {
                    Notify(player, "ID invalide.", NotificationManager.Type.Error);
                    return false;
                }
                product.IconId = iconId;
                return await product.Save();
            });
            panel.PreviousButton();
            panel.Display();
        }

        /// <summary>Étapes d'une formule (ex. Plat + Accompagnement + Boisson).</summary>
        private async void FormulaEditor(Player player, int productId)
        {
            DelivProduct product = await DelivProduct.Query(productId);
            if (product == null) return;
            List<int> slots = product.GetSlots();

            Panel panel = PanelHelper.Create($"Formule : {product.Name}", UIPanel.PanelType.Tab, player, () => FormulaEditor(player, productId));
            if (slots.Count == 0) panel.AddTabLine(Color("Aucune étape : formule fixe, sans choix", Colors.Grey), _ => { });
            for (int i = 0; i < slots.Count; i++)
            {
                int index = i;
                panel.AddTabLine($"Étape {i + 1} : {DelivCategory.Singular(slots[i])} au choix", async _ =>
                {
                    slots.RemoveAt(index);
                    product.SetSlots(slots);
                    await product.Save();
                    panel.Refresh();
                });
            }
            foreach (int category in DelivCategory.All.Where(c => c != DelivCategory.Formule))
            {
                int c = category;
                panel.AddButton($"+ {DelivCategory.Singular(category)}", async _ =>
                {
                    if (slots.Count >= 6) return;
                    slots.Add(c);
                    product.SetSlots(slots);
                    await product.Save();
                    panel.Refresh();
                });
            }
            if (slots.Count > 0) panel.AddButton(Color("Retirer l'étape", Colors.Error), _ => panel.SelectTab());
            panel.PreviousButton();
            panel.Display();
        }

        private static async Task SeedExampleMenu(int bizId)
        {
            (string name, int category, double price, string description)[] items =
            {
                ("Menu Classique", DelivCategory.Formule, 12.00, "Plat + accompagnement + boisson"),
                ("Menu Gourmand", DelivCategory.Formule, 15.00, "Plat + accompagnement + boisson + dessert"),
                ("Burger Classique", DelivCategory.Plat, 8.00, "Steak, cheddar, salade, tomate"),
                ("Double Cheese", DelivCategory.Plat, 9.50, "Deux steaks, double cheddar"),
                ("Wrap Poulet", DelivCategory.Plat, 7.50, "Poulet croustillant, crudités"),
                ("Pizza Margherita", DelivCategory.Plat, 9.00, null),
                ("Frites", DelivCategory.Accompagnement, 3.00, null),
                ("Potatoes", DelivCategory.Accompagnement, 3.50, null),
                ("Salade verte", DelivCategory.Accompagnement, 3.00, null),
                ("Soda", DelivCategory.Boisson, 2.50, "33 cl"),
                ("Eau minérale", DelivCategory.Boisson, 1.50, "50 cl"),
                ("Café", DelivCategory.Boisson, 1.50, null),
                ("Cookie", DelivCategory.Dessert, 2.00, null),
                ("Glace vanille", DelivCategory.Dessert, 3.00, null)
            };

            foreach (var item in items)
            {
                DelivProduct product = new DelivProduct
                {
                    BizId = bizId,
                    Name = item.name,
                    Category = item.category,
                    Price = item.price,
                    Description = item.description,
                    IsAvailable = true,
                    IconId = -1
                };
                if (item.name == "Menu Classique")
                    product.SetSlots(new List<int> { DelivCategory.Plat, DelivCategory.Accompagnement, DelivCategory.Boisson });
                if (item.name == "Menu Gourmand")
                    product.SetSlots(new List<int> { DelivCategory.Plat, DelivCategory.Accompagnement, DelivCategory.Boisson, DelivCategory.Dessert });
                await product.Save();
            }
        }

        // ------------------------------------------------------------------
        //  Paramètres du restaurant
        // ------------------------------------------------------------------

        private static string OnOff(bool value) => value ? Color("Oui", Colors.Success) : Color("Non", Colors.Error);

        public async void RestaurantSettings(Player player, int bizId)
        {
            DelivRestaurant r = await GetRestaurantByBiz(bizId);
            if (r == null || !await IsManager(player, r)) return;

            Panel panel = PanelHelper.Create($"Paramètres - {r.Name}", UIPanel.PanelType.Tab, player, () => RestaurantSettings(player, bizId));

            async void Toggle(Action change)
            {
                change();
                await r.Save();
                panel.Refresh();
            }

            panel.AddTabLine($"{Color("Nom affiché :", Colors.Info)} {r.Name}", _ => EditRestaurantText(player, r, "Nom affiché sur les bornes", false));
            panel.AddTabLine($"{Color("Message d'accueil :", Colors.Info)} {(string.IsNullOrWhiteSpace(r.WelcomeMessage) ? Color("par défaut", Colors.Grey) : Truncate(r.WelcomeMessage, 40))}",
                _ => EditRestaurantText(player, r, "Message d'accueil de la borne (vide = par défaut)", true));
            panel.AddTabLine($"{Color("Bornes ouvertes :", Colors.Info)} {OnOff(r.IsOpen)}", _ => Toggle(() => r.IsOpen = !r.IsOpen));
            panel.AddTabLine($"{Color("Paiement en espèces :", Colors.Info)} {OnOff(r.AcceptCash)}", _ => Toggle(() => r.AcceptCash = !r.AcceptCash));
            panel.AddTabLine($"{Color("Paiement par carte :", Colors.Info)} {OnOff(r.AcceptCard)}", _ => Toggle(() => r.AcceptCard = !r.AcceptCard));
            panel.AddTabLine($"{Color("Employé en service requis :", Colors.Info)} {OnOff(r.RequireStaffOnDuty)}",
                _ => Toggle(() => r.RequireStaffOnDuty = !r.RequireStaffOnDuty));
            panel.AddTabLine($"{Color("Remise des objets :", Colors.Info)} {OnOff(r.GiveItems)}", _ => Toggle(() => r.GiveItems = !r.GiveItems));
            panel.AddTabLine($"{Color("Objets pris chez l'employé :", Colors.Info)} {OnOff(r.TakeItemsFromEmployee)}",
                _ => Toggle(() => r.TakeItemsFromEmployee = !r.TakeItemsFromEmployee));
            panel.AddTabLine($"{Color("Webhook Discord :", Colors.Info)} {(string.IsNullOrWhiteSpace(r.WebhookUrl) ? Color("non configuré", Colors.Grey) : Color("configuré", Colors.Success))}",
                _ => EditWebhook(player, r));
            panel.AddTabLine($"{Color("Remettre les tickets à 001", Colors.Warning)} (actuel : {r.TicketCounter:000})", _ => Toggle(() => r.TicketCounter = 0));
            panel.AddTabLine(Color("Purger l'historique (+7 jours)", Colors.Warning), async _ =>
            {
                long limit = Now() - 7 * 86400;
                int id = r.BizId;
                List<DelivOrder> old = (await DelivOrder.Query(o => o.BizId == id && o.CreatedAt < limit)).Where(o => !o.IsActive).ToList();
                foreach (DelivOrder o in old) await o.Delete();
                Notify(player, $"{old.Count} commande(s) supprimée(s) de l'historique.", NotificationManager.Type.Success);
            });

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EditRestaurantText(Player player, DelivRestaurant r, string label, bool welcome)
        {
            Panel panel = PanelHelper.Create("Paramètres - Texte", UIPanel.PanelType.Input, player, () => EditRestaurantText(player, r, label, welcome));
            panel.TextLines.Add(label);
            panel.SetInputPlaceholder((welcome ? r.WelcomeMessage : r.DisplayName) ?? "");
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string value = panel.inputText?.Trim();
                if (welcome) r.WelcomeMessage = string.IsNullOrEmpty(value) ? null : Truncate(value, 200);
                else
                {
                    if (string.IsNullOrEmpty(value)) return false;
                    r.DisplayName = Truncate(value, 40);
                }
                return await r.Save();
            });
            panel.PreviousButton();
            panel.Display();
        }

        private void EditWebhook(Player player, DelivRestaurant r)
        {
            Panel panel = PanelHelper.Create("Webhook Discord", UIPanel.PanelType.Input, player, () => EditWebhook(player, r));
            panel.TextLines.Add("URL du webhook Discord (Salon > Modifier > Intégrations > Webhooks).");
            panel.TextLines.Add(Color("Nouvelles commandes, commandes prêtes, remises et annulations y seront envoyées. Vide = désactivé.", Colors.Grey));
            panel.SetInputPlaceholder("https://discord.com/api/webhooks/...");
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string url = panel.inputText?.Trim();
                if (!string.IsNullOrEmpty(url) && !url.StartsWith("https://"))
                {
                    Notify(player, "L'URL doit commencer par https://", NotificationManager.Type.Error);
                    return false;
                }
                r.WebhookUrl = string.IsNullOrEmpty(url) ? null : url;
                return await r.Save();
            });
            panel.AddButton("Tester", _ =>
            {
                if (string.IsNullOrWhiteSpace(r.WebhookUrl))
                {
                    Notify(player, "Aucun webhook enregistré.", NotificationManager.Type.Warning);
                    return;
                }
                SendWebhook(r, "#4287f5", $"{r.Name} — test", $"Webhook DelivLife opérationnel (test lancé par {player.FullName}).",
                    new List<string>(), new List<string>());
                Notify(player, "Message de test envoyé.", NotificationManager.Type.Success);
            });
            panel.PreviousButton();
            panel.Display();
        }
    }
}

namespace DelivLife
{
    // ======================================================================
    //  ADMINISTRATION (staff) : entreprises autorisées
    // ======================================================================
    public partial class DelivLifePlugin
    {
        public async void AdminHome(Player player)
        {
            if (!IsStaff(player))
            {
                Notify(player, "Vous devez être staff et en service admin.", NotificationManager.Type.Error);
                return;
            }

            List<DelivRestaurant> restaurants = await DelivRestaurant.QueryAll();

            Panel panel = PanelHelper.Create($"DelivLife by {Author} - Entreprises autorisées", UIPanel.PanelType.Tab, player, () => AdminHome(player));

            if (restaurants.Count == 0) panel.AddTabLine(Color("Aucune entreprise autorisée", Colors.Grey), _ => { });
            foreach (DelivRestaurant r in restaurants)
            {
                Bizs biz = Nova.biz.FetchBiz(r.BizId);
                string bizName = biz != null ? biz.BizName : Color("société introuvable", Colors.Error);
                panel.AddTabLine($"[{r.BizId}] {r.Name} {Color($"({bizName})", Colors.Grey)}", _ => AdminRestaurant(player, r.BizId));
            }

            if (restaurants.Count > 0) panel.NextButton("Gérer", () => panel.SelectTab());
            panel.NextButton(Color("Autoriser une entreprise", Colors.Success), () => AdminAddRestaurant(player));
            panel.NextButton("Points clients", () => new DelivKiosk(false) { Context = this }.CreateOrGenerate(player));
            panel.NextButton("Points employés", () => new DelivCounter(false) { Context = this }.CreateOrGenerate(player));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void AdminAddRestaurant(Player player)
        {
            List<int> already = (await DelivRestaurant.QueryAll()).Select(r => r.BizId).ToList();
            List<Bizs> bizs = Nova.biz.bizs.Where(b => !already.Contains(b.Id)).OrderBy(b => b.Id).ToList();

            Panel panel = PanelHelper.Create("DelivLife - Autoriser une entreprise", UIPanel.PanelType.Tab, player, () => AdminAddRestaurant(player));
            if (bizs.Count == 0) panel.AddTabLine("Aucune autre entreprise", _ => { });
            foreach (Bizs biz in bizs)
            {
                panel.AddTabLine($"[{biz.Id}] {biz.BizName}", async _ =>
                {
                    DelivRestaurant r = new DelivRestaurant
                    {
                        BizId = biz.Id,
                        DisplayName = BizUtils.BizNameWithoutId(biz.BizName),
                        IsOpen = true,
                        AcceptCash = true,
                        AcceptCard = true,
                        GiveItems = true
                    };
                    if (await r.Save())
                    {
                        Notify(player, $"« {biz.BizName} » a maintenant accès à DelivLife.", NotificationManager.Type.Success);
                        panel.Previous();
                    }
                });
            }
            if (bizs.Count > 0) panel.AddButton("Autoriser", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void AdminRestaurant(Player player, int bizId)
        {
            DelivRestaurant r = await GetRestaurantByBiz(bizId);
            if (r == null) return;

            Panel panel = PanelHelper.Create($"DelivLife - {r.Name}", UIPanel.PanelType.Tab, player, () => AdminRestaurant(player, bizId));
            panel.AddTabLine("Menu employé (commandes, caisse...)", _ => EmployeeHome(player, bizId));
            panel.AddTabLine("Gestion de la carte", _ => MenuManager(player, bizId));
            panel.AddTabLine("Paramètres", _ => RestaurantSettings(player, bizId));
            panel.AddTabLine(Color("Retirer l'accès", Colors.Error), _ => AdminRemoveRestaurant(player, bizId));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void AdminRemoveRestaurant(Player player, int bizId)
        {
            DelivRestaurant r = await GetRestaurantByBiz(bizId);
            if (r == null) return;

            Panel panel = PanelHelper.Create("Confirmation", UIPanel.PanelType.Text, player, () => AdminRemoveRestaurant(player, bizId));
            panel.TextLines.Add($"Retirer l'accès DelivLife à « {r.Name} » ?");
            panel.TextLines.Add(Color("La carte et l'historique des commandes seront supprimés. Les bornes liées ne fonctionneront plus.", Colors.Error));
            panel.CloseButtonWithAction(Color("Confirmer", Colors.Error), async () =>
            {
                foreach (DelivProduct p in await DelivProduct.Query(p => p.BizId == bizId)) await p.Delete();
                foreach (DelivOrder o in await DelivOrder.Query(o => o.BizId == bizId)) await o.Delete();
                bool ok = await r.Delete();
                Notify(player, ok ? "Accès retiré." : "Erreur lors de la suppression.", ok ? NotificationManager.Type.Success : NotificationManager.Type.Error);
                return ok;
            });
            panel.PreviousButton("Annuler");
            panel.Display();
        }
    }
}

namespace DelivLife
{
    // ======================================================================
    //  POINTS BLEUS : borne de commande (clients) et comptoir (employés)
    // ======================================================================

    /// <summary>Base commune des points DelivLife : un modèle = un nom + un restaurant.</summary>
    public abstract class DelivPointBase<T> : ModEntity<T>, PatternData where T : DelivPointBase<T>, new()
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }

        public int BizId { get; set; }

        /// <summary>Fonction du point (voir FunctionNames).</summary>
        public int Function { get; set; }

        [Ignore]
        public string TypeName { get; set; } = typeof(T).Name;

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        protected abstract string Label { get; }

        /// <summary>Fonctions possibles pour ce type de point (l'index est stocké dans Function).</summary>
        protected abstract string[] FunctionNames { get; }

        protected string FunctionName(int function) =>
            function >= 0 && function < FunctionNames.Length ? FunctionNames[function] : FunctionNames[0];

        protected DelivLifePlugin Plugin => DelivLifePlugin.Instance;

        public abstract void OnPlayerTrigger(Player player);

        public async Task SetProperties(int id)
        {
            T result = await Query(id);
            Id = id;
            TypeName = typeof(T).Name;
            PatternName = result?.PatternName;
            BizId = result?.BizId ?? 0;
            Function = result?.Function ?? 0;
        }

        private T Copy(T source)
        {
            source.TypeName = typeof(T).Name;
            source.Context = Context;
            return source;
        }

        /// <summary>Menu staff : choisir un modèle et le placer à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!player.IsAdmin) return;
            List<T> patterns = await QueryAll();
            Dictionary<int, string> names = (await DelivRestaurant.QueryAll()).ToDictionary(r => r.BizId, r => r.Name);

            Panel panel = Context.PanelHelper.Create($"DelivLife - {Label}s", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));
            if (patterns.Count == 0) panel.AddTabLine("Aucun modèle, créez-en un", _ => { });
            foreach (T pattern in patterns)
            {
                string restaurant = names.TryGetValue(pattern.BizId, out string n) ? n : Color("restaurant supprimé", Colors.Error);
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName} {Color($"({FunctionName(pattern.Function)} - {restaurant})", Colors.Grey)}", async _ =>
                {
                    if (await Context.PointHelper.CreateNPoint(player, Copy(pattern)))
                        DelivLifePlugin.Notify(player, $"{Label} « {pattern.PatternName} » placé(e) à votre position.", NotificationManager.Type.Success);
                });
            }
            if (patterns.Count > 0) panel.NextButton("Placer ici", () => panel.SelectTab());
            panel.NextButton("Nouveau modèle", () => SetPatternData(player));
            panel.NextButton("Modèles", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Nouveau modèle : choisir le restaurant puis le nom.</summary>
        public async void SetPatternData(Player player)
        {
            List<DelivRestaurant> restaurants = await DelivRestaurant.QueryAll();
            Panel panel = Context.PanelHelper.Create($"Nouveau modèle - {Label} - Restaurant", UIPanel.PanelType.Tab, player, () => SetPatternData(player));
            if (restaurants.Count == 0) panel.AddTabLine("Autorisez d'abord une entreprise (/delivadmin)", _ => { });
            foreach (DelivRestaurant r in restaurants)
            {
                panel.AddTabLine($"[{r.BizId}] {r.Name}", _ => SetPatternFunction(player, r));
            }
            if (restaurants.Count > 0) panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Choix de la fonction du point (ex. borne, caisse, écran cuisine...).</summary>
        private void SetPatternFunction(Player player, DelivRestaurant restaurant)
        {
            Panel panel = Context.PanelHelper.Create($"Nouveau modèle - {Label} - Fonction", UIPanel.PanelType.Tab, player, () => SetPatternFunction(player, restaurant));
            for (int i = 0; i < FunctionNames.Length; i++)
            {
                int function = i;
                panel.AddTabLine(FunctionNames[i], _ => SetPatternName(player, restaurant, function));
            }
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void SetPatternName(Player player, DelivRestaurant restaurant, int function)
        {
            string defaultName = $"{FunctionName(function)} - {restaurant.Name}";
            Panel panel = Context.PanelHelper.Create($"Nouveau modèle - {Label} - Nom", UIPanel.PanelType.Input, player, () => SetPatternName(player, restaurant, function));
            panel.TextLines.Add($"Nom du modèle ({FunctionName(function).ToLower()} de « {restaurant.Name} »)");
            panel.SetInputPlaceholder(defaultName);
            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name)) name = defaultName;
                T pattern = new T { PatternName = name, BizId = restaurant.BizId, Function = function };
                if (!await pattern.Save()) return false;
                DelivLifePlugin.Notify(player, $"Modèle « {name} » créé. Choisissez-le puis « Placer ici ».", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<T> patterns = await QueryAll();
            string action = "";
            Panel panel = Context.PanelHelper.Create($"DelivLife - Modèles de {Label.ToLower()}", UIPanel.PanelType.Tab, player,
                async () => await GetPatternData(player, forEdit));
            if (patterns.Count == 0) panel.AddTabLine("Aucun modèle", _ => { });
            foreach (T pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName} {Color($"({FunctionName(pattern.Function)})", Colors.Grey)}", async _ =>
                {
                    if (action != "delete") return;
                    await Context.PointHelper.DeleteNPointsByPattern(player, Copy(pattern));
                    await pattern.Delete();
                    DelivLifePlugin.Notify(player, $"Modèle « {pattern.PatternName} » et ses points supprimés.", NotificationManager.Type.Success);
                    panel.Refresh();
                });
            }
            if (patterns.Count > 0 && forEdit)
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async Task GetNPoints(Player player)
        {
            string typeName = typeof(T).Name;
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == typeName);
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Context.PanelHelper.Create($"DelivLife - {Label}s placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));
            if (points.Count == 0) panel.AddTabLine("Aucun point placé", _ => { });
            foreach (NPoint point in points)
            {
                string name = names.TryGetValue(point.PatternId, out string n) ? n : "?";
                panel.AddTabLine($"Point #{point.Id} - {name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            Context.PointHelper.PlayerSetPositionToNPoint(player, point);
                            break;
                        case "move":
                            if (await Context.PointHelper.SetNPointPosition(player, point))
                                DelivLifePlugin.Notify(player, "Point déplacé à votre position.", NotificationManager.Type.Success);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPoint(point);
                            DelivLifePlugin.Notify(player, $"Point #{point.Id} supprimé.", NotificationManager.Type.Success);
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
            panel.Display();
        }
    }

    /// <summary>Points natifs côté client.</summary>
    public class DelivKiosk : DelivPointBase<DelivKiosk>
    {
        public const int Kiosk = 0;
        public const int Board = 1;
        public const int Pickup = 2;
        public const int Tracking = 3;

        private static readonly string[] Functions =
        {
            "Borne de commande",
            "Écran des commandes",
            "Retrait des commandes",
            "Suivi de mes commandes"
        };

        public DelivKiosk() { }

        public DelivKiosk(bool isCreated) { TypeName = nameof(DelivKiosk); }

        protected override string Label => "Point client";

        protected override string[] FunctionNames => Functions;

        public override void OnPlayerTrigger(Player player)
        {
            // Le modèle est partagé : on capture ses valeurs tout de suite
            int bizId = BizId;
            int function = Function;
            Plugin?.OpenClientPoint(player, bizId, function);
        }
    }

    /// <summary>Points natifs côté employés (réservés au personnel du restaurant).</summary>
    public class DelivCounter : DelivPointBase<DelivCounter>
    {
        public const int Full = 0;
        public const int Register = 1;
        public const int Kitchen = 2;
        public const int Duty = 3;
        public const int Menu = 4;
        public const int Stats = 5;

        private static readonly string[] Functions =
        {
            "Comptoir (menu employé complet)",
            "Caisse enregistreuse",
            "Écran cuisine (commandes en cours)",
            "Pointeuse (prise / fin de service)",
            "Gestion de la carte (gérant)",
            "Bureau (historique et statistiques)"
        };

        public DelivCounter() { }

        public DelivCounter(bool isCreated) { TypeName = nameof(DelivCounter); }

        protected override string Label => "Point employé";

        protected override string[] FunctionNames => Functions;

        public override void OnPlayerTrigger(Player player)
        {
            int bizId = BizId;
            int function = Function;
            if (Plugin == null) return;
            if (player.character != null && player.character.BizId == bizId || player.IsAdmin && player.serviceAdmin)
                Plugin.OpenEmployeePoint(player, bizId, function);
            else
                DelivLifePlugin.Notify(player, "Réservé au personnel du restaurant.", NotificationManager.Type.Warning);
        }
    }
}
