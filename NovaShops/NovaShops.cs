using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.DB;
using Life.Network;
using Life.PermissionSystem;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using Newtonsoft.Json;
using SQLite;

namespace NovaShops
{
    /// <summary>
    /// Boutiques d'items (achat / revente) et concessionnaires, placés en points bleus par le staff.
    /// By Matheo Mercier.
    /// </summary>
    public class NovaShopsPlugin : ModKit.ModKit
    {
        public const string Signature = "By Matheo Mercier";

        public NovaShopsPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.1.0", "Matheo Mercier");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            Orm.RegisterTable<ItemShop>();
            Orm.RegisterTable<VehicleShop>();
            Orm.RegisterTable<ShopEntry>();

            ItemShop itemShop = new ItemShop(this);
            VehicleShop vehicleShop = new VehicleShop(this);
            PointHelper.AddPattern(nameof(ItemShop), itemShop);
            PointHelper.AddPattern(nameof(VehicleShop), vehicleShop);

            if (AAMenu.AAMenu.menu != null)
            {
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, nameof(ItemShop), itemShop, this);
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, nameof(VehicleShop), vehicleShop, this);
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 5, "Boutiques", ui =>
                {
                    Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                    ShopUi.ManageShops(this, player);
                });
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez la commande /boutiques.");
            }

            RegisterCommands();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version} - {Signature}", "initialisé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            // Recrée chez le joueur tous les points bleus enregistrés en base.
            // Sans cet appel, les boutiques « disparaissent » après un redémarrage du serveur.
            PointHelper.InitAllNPoint(player);
        }

        private void RegisterCommands()
        {
            // Accès staff direct à la gestion des boutiques (si AAMenu bug ou pour replacer un point)
            new SChatCommand("/boutiques", new[] { "/shops" }, "Gérer les boutiques (staff)", "/boutiques",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!ShopUi.IsStaff(player)) return;
                    ShopUi.ManageShops(this, player);
                })).Register();
        }
    }

    public static class Kind
    {
        public const int Item = 0;
        public const int Vehicle = 1;
    }

    /// <summary>Une ligne de boutique : un item ou un modèle de voiture avec ses prix.</summary>
    [Table("NS_ShopEntries")]
    public class ShopEntry : ModEntity<ShopEntry>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int ShopKind { get; set; }

        public int ShopId { get; set; }

        public int ObjectId { get; set; }

        public string Name { get; set; }

        public double BuyPrice { get; set; }

        public double SellPrice { get; set; }
    }

    [Table("NS_ItemShops")]
    public class ItemShop : ModEntity<ItemShop>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string TypeName { get; set; }

        public string PatternName { get; set; }

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public ItemShop() { }

        public ItemShop(ModKit.ModKit context)
        {
            Context = context;
            TypeName = nameof(ItemShop);
        }

        public async Task SetProperties(int id)
        {
            ItemShop shop = await Query(id);
            Id = id;
            TypeName = nameof(ItemShop);
            PatternName = shop?.PatternName;
        }

        public void OnPlayerTrigger(Player player) => ShopUi.OpenShop(Context, player, Kind.Item, Id, PatternName);

        public async Task GetPatternData(Player player, bool forEdit)
        {
            await SetProperties(Id);
            if (forEdit) ShopUi.AdminShop(Context, player, Kind.Item, Id, PatternName);
            else OnPlayerTrigger(player);
        }

        public Task GetNPoints(Player player) => ShopUi.ListPoints(Context, player, Kind.Item);

        public void SetPatternData(Player player) => ShopUi.CreateShop(Context, player, Kind.Item);

        public void CreateOrGenerate(Player player) => ShopUi.PlaceMenu(Context, player, Kind.Item);
    }

    [Table("NS_VehicleShops")]
    public class VehicleShop : ModEntity<VehicleShop>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string TypeName { get; set; }

        public string PatternName { get; set; }

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public VehicleShop() { }

        public VehicleShop(ModKit.ModKit context)
        {
            Context = context;
            TypeName = nameof(VehicleShop);
        }

        public async Task SetProperties(int id)
        {
            VehicleShop shop = await Query(id);
            Id = id;
            TypeName = nameof(VehicleShop);
            PatternName = shop?.PatternName;
        }

        public void OnPlayerTrigger(Player player) => ShopUi.OpenShop(Context, player, Kind.Vehicle, Id, PatternName);

        public async Task GetPatternData(Player player, bool forEdit)
        {
            await SetProperties(Id);
            if (forEdit) ShopUi.AdminShop(Context, player, Kind.Vehicle, Id, PatternName);
            else OnPlayerTrigger(player);
        }

        public Task GetNPoints(Player player) => ShopUi.ListPoints(Context, player, Kind.Vehicle);

        public void SetPatternData(Player player) => ShopUi.CreateShop(Context, player, Kind.Vehicle);

        public void CreateOrGenerate(Player player) => ShopUi.PlaceMenu(Context, player, Kind.Vehicle);
    }

    /// <summary>Livraison des voitures achetées au garage public.</summary>
    public static class Garage
    {
        private const string Letters = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        private static readonly Random Rng = new Random();

        private static char L() => Letters[Rng.Next(Letters.Length)];

        private static string NewPlate() => $"{L()}{L()}-{Rng.Next(100, 1000)}-{L()}{L()}";

        public static async Task<bool> GiveToPublicGarage(Player player, int modelId)
        {
            try
            {
                Permissions perms = new Permissions
                {
                    owner = new Entity { characterId = player.character.Id, groupId = 0u }
                };
                Vehicles vehicle = await LifeDB.CreateVehicle(modelId, JsonConvert.SerializeObject(perms));
                if (vehicle == null) return false;

                vehicle.Plate = NewPlate();
                vehicle.IsStowed = true;
                await LifeDB.db.UpdateAsync(vehicle);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[NovaShops] Garage error: " + ex);
                return false;
            }
        }
    }

    public static class ShopUi
    {
        private const string Title = "Boutique";

        private const string Green = "#4caf50";
        private const string Red = "#e53935";
        private const string Orange = "#ff9800";
        private const string Blue = "#42a5f5";

        private static string Color(string text, string hex) => $"<color={hex}>{text}</color>";

        public static bool IsStaff(Player p)
        {
            if (p.IsAdmin && p.serviceAdmin) return true;
            p.Notify(Title, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
            return false;
        }

        private static void Ok(Player p, string msg) => p.Notify(Title, msg, NotificationManager.Type.Success, 5f);

        private static void Err(Player p, string msg) => p.Notify(Title, msg, NotificationManager.Type.Error, 5f);

        private static string TypeOf(int kind) => kind == Kind.Item ? nameof(ItemShop) : nameof(VehicleShop);

        /// <summary>Petite fenêtre de saisie réutilisée par tous les formulaires.</summary>
        public static void Ask(ModKit.ModKit ctx, Player p, string title, string hint, Action<string> next)
        {
            Panel panel = ctx.PanelHelper.Create(title, UIPanel.PanelType.Input, p, () => Ask(ctx, p, title, hint, next));
            panel.TextLines.Add(hint);
            panel.SetInputPlaceholder(hint);
            panel.NextButton("Valider", () => next(panel.inputText));
            panel.CloseButton("Annuler");
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Paiement : espèces ou banque
        // ------------------------------------------------------------------

        private static double Cash(Player p) => p.Money;

        private static double Bank(Player p) => p.character.Bank;

        /// <summary>
        /// Demande au joueur comment payer <paramref name="price"/> €.
        /// <paramref name="deliver"/> donne l'objet acheté et renvoie false en cas d'échec :
        /// le joueur n'est alors pas débité.
        /// </summary>
        private static void AskPayment(ModKit.ModKit ctx, Player p, string what, double price, Func<Task<bool>> deliver, Action back)
        {
            Panel panel = ctx.PanelHelper.Create("Paiement", UIPanel.PanelType.Text, p, () => AskPayment(ctx, p, what, price, deliver, back));
            panel.TextLines.Add($"Achat : {Color(what, Blue)}");
            panel.TextLines.Add($"Prix : {Color($"{price:0.##}€", Orange)}");
            panel.TextLines.Add($"Espèces : {Cash(p):0.##}€  |  Banque : {Bank(p):0.##}€");
            panel.TextLines.Add($"<size=70%>{NovaShopsPlugin.Signature}</size>");

            panel.NextButton("Espèces", async () => await Pay(p, price, false, deliver, back));
            panel.NextButton("Carte bancaire", async () => await Pay(p, price, true, deliver, back));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async Task Pay(Player p, double price, bool bank, Func<Task<bool>> deliver, Action back)
        {
            double available = bank ? Bank(p) : Cash(p);
            if (available < price)
            {
                Err(p, $"Il vous manque {price - available:0.##}€ {(bank ? "en banque" : "en espèces")}.");
                back();
                return;
            }

            if (!await deliver())
            {
                back();
                return;
            }

            if (bank) p.AddBankMoney(-price);
            else p.AddMoney(-price, "Achat boutique");

            Ok(p, $"Payé {price:0.##}€ {(bank ? "par carte" : "en espèces")}.");
            back();
        }

        // ------------------------------------------------------------------
        //  Côté joueur
        // ------------------------------------------------------------------

        public static void OpenShop(ModKit.ModKit ctx, Player p, int kind, int shopId, string title)
        {
            if (kind == Kind.Vehicle)
            {
                VehicleList(ctx, p, shopId, title);
                return;
            }

            Panel panel = ctx.PanelHelper.Create(title ?? Title, UIPanel.PanelType.Tab, p, () => OpenShop(ctx, p, kind, shopId, title));
            panel.AddTabLine("Acheter", _ => BuyList(ctx, p, shopId, title));
            panel.AddTabLine("Vendre", _ => SellList(ctx, p, shopId, title));
            panel.NextButton("Choisir", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        private static async void BuyList(ModKit.ModKit ctx, Player p, int shopId, string title)
        {
            List<ShopEntry> list = await ShopEntry.Query(e => e.ShopKind == Kind.Item && e.ShopId == shopId && e.BuyPrice > 0.0);

            Panel panel = ctx.PanelHelper.Create($"{title} - Achat", UIPanel.PanelType.TabPrice, p, () => BuyList(ctx, p, shopId, title));
            if (list.Count == 0) panel.AddTabLine("Rien à vendre ici", _ => { });
            foreach (ShopEntry entry in list)
            {
                panel.AddTabLine($"{entry.Name}", $"{entry.BuyPrice:0.##}€", ItemUtils.GetIconIdByItemId(entry.ObjectId), _ =>
                    Ask(ctx, p, entry.Name, "Quantité", q => BuyItem(ctx, p, entry, q, () => BuyList(ctx, p, shopId, title))));
            }
            if (list.Count > 0) panel.NextButton("Choisir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void BuyItem(ModKit.ModKit ctx, Player p, ShopEntry e, string qtyText, Action back)
        {
            if (!int.TryParse(qtyText, out int qty) || qty <= 0)
            {
                Err(p, "Quantité invalide.");
                back();
                return;
            }

            AskPayment(ctx, p, $"{qty}x {e.Name}", e.BuyPrice * qty, () =>
            {
                if (InventoryUtils.AddItem(p, e.ObjectId, qty)) return Task.FromResult(true);
                Err(p, "Inventaire plein.");
                return Task.FromResult(false);
            }, back);
        }

        private static async void SellList(ModKit.ModKit ctx, Player p, int shopId, string title)
        {
            List<ShopEntry> list = await ShopEntry.Query(e => e.ShopKind == Kind.Item && e.ShopId == shopId && e.SellPrice > 0.0);
            list = list.Where(e => InventoryUtils.CheckInventoryContainsItem(p, e.ObjectId, 1)).ToList();

            Panel panel = ctx.PanelHelper.Create($"{title} - Vente", UIPanel.PanelType.TabPrice, p, () => SellList(ctx, p, shopId, title));
            if (list.Count == 0) panel.AddTabLine("Vous n'avez rien à vendre ici", _ => { });
            foreach (ShopEntry entry in list)
            {
                panel.AddTabLine($"{entry.Name}", $"{entry.SellPrice:0.##}€", ItemUtils.GetIconIdByItemId(entry.ObjectId), _ =>
                    Ask(ctx, p, entry.Name, "Quantité à vendre", q =>
                    {
                        SellItem(p, entry, q);
                        SellList(ctx, p, shopId, title);
                    }));
            }
            if (list.Count > 0) panel.NextButton("Choisir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void SellItem(Player p, ShopEntry e, string qtyText)
        {
            if (!int.TryParse(qtyText, out int qty) || qty <= 0)
            {
                Err(p, "Quantité invalide.");
                return;
            }
            if (!InventoryUtils.CheckInventoryContainsItem(p, e.ObjectId, qty))
            {
                Err(p, "Vous n'en avez pas assez.");
                return;
            }
            int removed = InventoryUtils.RemoveFromInventory(p, e.ObjectId, qty);
            if (removed <= 0)
            {
                Err(p, "Vente impossible.");
                return;
            }
            double gain = e.SellPrice * removed;
            p.AddMoney(gain, "Vente boutique");
            Ok(p, $"Vendu : {removed}x {e.Name} pour {gain:0.##}€ (espèces).");
        }

        private static async void VehicleList(ModKit.ModKit ctx, Player p, int shopId, string title)
        {
            List<ShopEntry> list = await ShopEntry.Query(e => e.ShopKind == Kind.Vehicle && e.ShopId == shopId);

            Panel panel = ctx.PanelHelper.Create(title ?? "Concessionnaire", UIPanel.PanelType.TabPrice, p, () => VehicleList(ctx, p, shopId, title));
            if (list.Count == 0) panel.AddTabLine("Aucun véhicule en vente", _ => { });
            foreach (ShopEntry entry in list)
            {
                panel.AddTabLine(entry.Name, $"{entry.BuyPrice:0.##}€", VehicleUtils.GetIconId(entry.ObjectId), _ =>
                    AskPayment(ctx, p, $"{entry.Name} (livrée au garage public)", entry.BuyPrice, async () =>
                    {
                        if (await Garage.GiveToPublicGarage(p, entry.ObjectId))
                        {
                            Ok(p, $"{entry.Name} achetée ! Récupérez-la au garage public.");
                            return true;
                        }
                        Err(p, "Erreur : achat annulé, vous n'avez pas été débité.");
                        return false;
                    }, () => VehicleList(ctx, p, shopId, title)));
            }
            if (list.Count > 0) panel.NextButton("Choisir", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Côté staff : boutiques
        // ------------------------------------------------------------------

        public static async void ManageShops(ModKit.ModKit ctx, Player p)
        {
            if (!p.IsAdmin) return;

            List<ItemShop> items = await ItemShop.QueryAll();
            List<VehicleShop> vehicles = await VehicleShop.QueryAll();

            Panel panel = ctx.PanelHelper.Create($"Boutiques - {NovaShopsPlugin.Signature}", UIPanel.PanelType.Tab, p, () => ManageShops(ctx, p));
            panel.AddTabLine(Color("+ Créer une boutique d'items ici", Green), _ => CreateShop(ctx, p, Kind.Item));
            panel.AddTabLine(Color("+ Créer un concessionnaire ici", Green), _ => CreateShop(ctx, p, Kind.Vehicle));
            panel.AddTabLine(Color("Points bleus des boutiques d'items", Blue), async _ => await ListPoints(ctx, p, Kind.Item));
            panel.AddTabLine(Color("Points bleus des concessionnaires", Blue), async _ => await ListPoints(ctx, p, Kind.Vehicle));
            foreach (ItemShop sh in items)
                panel.AddTabLine($"[Items] {sh.PatternName} (#{sh.Id})", _ => AdminShop(ctx, p, Kind.Item, sh.Id, sh.PatternName));
            foreach (VehicleShop sh in vehicles)
                panel.AddTabLine($"[Voitures] {sh.PatternName} (#{sh.Id})", _ => AdminShop(ctx, p, Kind.Vehicle, sh.Id, sh.PatternName));
            panel.NextButton("Choisir", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        public static void CreateShop(ModKit.ModKit ctx, Player p, int kind)
        {
            string title = kind == Kind.Item ? "Nouvelle boutique d'items" : "Nouveau concessionnaire";
            Ask(ctx, p, title, "Nom (ex: Supermarché)", async name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    Err(p, "Nom invalide.");
                    return;
                }
                try
                {
                    name = name.Trim();
                    PatternData pattern;
                    bool ok;
                    if (kind == Kind.Item)
                    {
                        ItemShop s = new ItemShop(ctx) { PatternName = name };
                        ok = await s.Save();
                        pattern = s;
                    }
                    else
                    {
                        VehicleShop s = new VehicleShop(ctx) { PatternName = name };
                        ok = await s.Save();
                        pattern = s;
                    }
                    if (!ok)
                    {
                        Err(p, "Erreur : sauvegarde impossible.");
                        return;
                    }
                    await PlacePoint(ctx, p, pattern);
                    AdminShop(ctx, p, kind, pattern.Id, name);
                }
                catch (Exception ex)
                {
                    Err(p, "Erreur : " + ex.Message);
                    Console.WriteLine("[NovaShops] CreateShop: " + ex);
                }
            });
        }

        /// <summary>Crée un point bleu de la boutique à la position du staff.</summary>
        private static async Task PlacePoint(ModKit.ModKit ctx, Player p, PatternData pattern)
        {
            if (await ctx.PointHelper.CreateNPoint(p, pattern))
                Ok(p, $"Point bleu « {pattern.PatternName} » placé à votre position.");
            else
                Err(p, "Erreur lors de la création du point.");
        }

        private static async Task<PatternData> LoadShop(ModKit.ModKit ctx, int kind, int shopId)
        {
            if (kind == Kind.Item)
            {
                ItemShop s = await ItemShop.Query(shopId);
                if (s == null) return null;
                s.Context = ctx;
                s.TypeName = nameof(ItemShop);
                return s;
            }
            VehicleShop v = await VehicleShop.Query(shopId);
            if (v == null) return null;
            v.Context = ctx;
            v.TypeName = nameof(VehicleShop);
            return v;
        }

        /// <summary>AAMenu « Créer / générer » : choisir une boutique existante et y ajouter un point ici.</summary>
        public static async void PlaceMenu(ModKit.ModKit ctx, Player p, int kind)
        {
            if (!p.IsAdmin) return;

            List<(int Id, string Name)> shops = kind == Kind.Item
                ? (await ItemShop.QueryAll()).Select(s => (s.Id, s.PatternName)).ToList()
                : (await VehicleShop.QueryAll()).Select(s => (s.Id, s.PatternName)).ToList();

            Panel panel = ctx.PanelHelper.Create("Placer un point bleu ici", UIPanel.PanelType.Tab, p, () => PlaceMenu(ctx, p, kind));
            panel.AddTabLine(Color("+ Nouvelle boutique", Green), _ => CreateShop(ctx, p, kind));
            foreach ((int id, string name) in shops)
            {
                panel.AddTabLine($"{name} (#{id})", async _ =>
                {
                    PatternData pattern = await LoadShop(ctx, kind, id);
                    if (pattern != null) await PlacePoint(ctx, p, pattern);
                    panel.Refresh();
                });
            }
            panel.NextButton("Placer ici", () => panel.SelectTab());
            panel.NextButton("Points", async () => await ListPoints(ctx, p, kind));
            panel.CloseButton();
            panel.Display();
        }

        public static async void AdminShop(ModKit.ModKit ctx, Player p, int kind, int shopId, string title)
        {
            List<ShopEntry> list = await ShopEntry.Query(e => e.ShopKind == kind && e.ShopId == shopId);

            Panel panel = ctx.PanelHelper.Create($"Admin - {title}", UIPanel.PanelType.TabPrice, p, () => AdminShop(ctx, p, kind, shopId, title));
            foreach (ShopEntry entry in list)
            {
                string price = kind == Kind.Item ? $"{entry.BuyPrice:0.##}/{entry.SellPrice:0.##}€" : $"{entry.BuyPrice:0.##}€";
                int icon = kind == Kind.Item ? ItemUtils.GetIconIdByItemId(entry.ObjectId) : VehicleUtils.GetIconId(entry.ObjectId);
                panel.AddTabLine($"{entry.Name} (ID {entry.ObjectId})", price, icon, async _ =>
                {
                    await entry.Delete();
                    Ok(p, $"{entry.Name} supprimé.");
                    AdminShop(ctx, p, kind, shopId, title);
                });
            }
            if (list.Count > 0) panel.NextButton("Supprimer la ligne", () => panel.SelectTab());
            panel.NextButton("+ Ajouter", () => AddEntry(ctx, p, kind, shopId, title));
            panel.NextButton("Placer un point ici", async () =>
            {
                PatternData pattern = await LoadShop(ctx, kind, shopId);
                if (pattern != null) await PlacePoint(ctx, p, pattern);
                else Err(p, "Boutique introuvable.");
            });
            panel.NextButton("Points", async () => await ListPoints(ctx, p, kind, shopId));
            panel.NextButton(Color("Supprimer la boutique", Red), () => ConfirmDeleteShop(ctx, p, kind, shopId, title));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void AddEntry(ModKit.ModKit ctx, Player p, int kind, int shopId, string title)
        {
            Action back = () => AdminShop(ctx, p, kind, shopId, title);
            string hint = kind == Kind.Item ? "ID de l'item" : "ID du modèle de voiture";
            Ask(ctx, p, "Ajouter", hint, idText =>
            {
                if (!int.TryParse(idText, out int objectId))
                {
                    Err(p, "ID invalide.");
                    back();
                    return;
                }
                Ask(ctx, p, "Ajouter", "Nom affiché", name =>
                {
                    Ask(ctx, p, "Ajouter", "Prix d'achat (le joueur paie)", buyText =>
                    {
                        if (!double.TryParse(buyText, out double buy) || buy < 0.0)
                        {
                            Err(p, "Prix invalide.");
                            back();
                            return;
                        }
                        if (kind == Kind.Vehicle)
                        {
                            SaveEntry(ctx, p, kind, shopId, title, objectId, name, buy, 0.0);
                            return;
                        }
                        Ask(ctx, p, "Ajouter", "Prix de rachat (0 = pas de rachat)", sellText =>
                        {
                            if (!double.TryParse(sellText, out double sell) || sell < 0.0)
                            {
                                Err(p, "Prix invalide.");
                                back();
                                return;
                            }
                            SaveEntry(ctx, p, kind, shopId, title, objectId, name, buy, sell);
                        });
                    });
                });
            });
        }

        private static async void SaveEntry(ModKit.ModKit ctx, Player p, int kind, int shopId, string title, int objectId, string name, double buy, double sell)
        {
            ShopEntry e = new ShopEntry
            {
                ShopKind = kind,
                ShopId = shopId,
                ObjectId = objectId,
                Name = name,
                BuyPrice = buy,
                SellPrice = sell
            };
            if (await e.Save()) Ok(p, $"{name} ajouté.");
            else Err(p, "Erreur de sauvegarde.");
            AdminShop(ctx, p, kind, shopId, title);
        }

        private static void ConfirmDeleteShop(ModKit.ModKit ctx, Player p, int kind, int shopId, string title)
        {
            Panel panel = ctx.PanelHelper.Create("Supprimer la boutique", UIPanel.PanelType.Text, p, () => ConfirmDeleteShop(ctx, p, kind, shopId, title));
            panel.TextLines.Add($"Supprimer {title} (#{shopId}) ?");
            panel.TextLines.Add(Color("Les points bleus et tout le contenu seront effacés définitivement.", Orange));
            panel.NextButton(Color("Oui, supprimer", Red), () => DeleteShop(ctx, p, kind, shopId, title));
            panel.PreviousButton("Non, retour");
            panel.CloseButton();
            panel.Display();
        }

        private static async void DeleteShop(ModKit.ModKit ctx, Player p, int kind, int shopId, string title)
        {
            try
            {
                PatternData pattern = await LoadShop(ctx, kind, shopId);
                if (pattern != null) await ctx.PointHelper.DeleteNPointsByPattern(p, pattern);

                List<ShopEntry> entries = await ShopEntry.Query(e => e.ShopKind == kind && e.ShopId == shopId);
                foreach (ShopEntry entry in entries)
                    await entry.Delete();

                if (pattern is ItemShop item) await item.Delete();
                else if (pattern is VehicleShop vehicle) await vehicle.Delete();

                Ok(p, $"Boutique « {title} » supprimée.");
            }
            catch (Exception ex)
            {
                Err(p, "Erreur lors de la suppression : " + ex.Message);
                Console.WriteLine("[NovaShops] DeleteShop: " + ex);
            }
            ManageShops(ctx, p);
        }

        // ------------------------------------------------------------------
        //  Côté staff : points bleus placés (réparer / replacer en cas de bug)
        // ------------------------------------------------------------------

        /// <summary>Liste des points bleus placés : se téléporter, déplacer ici ou supprimer.</summary>
        public static async Task ListPoints(ModKit.ModKit ctx, Player p, int kind, int shopId = -1)
        {
            if (!p.IsAdmin) return;

            string typeName = TypeOf(kind);
            List<NPoint> points = await ModEntity<NPoint>.Query(n => n.TypeName == typeName);
            if (shopId >= 0) points = points.Where(n => n.PatternId == shopId).ToList();

            Dictionary<int, string> names = kind == Kind.Item
                ? (await ItemShop.QueryAll()).ToDictionary(s => s.Id, s => s.PatternName)
                : (await VehicleShop.QueryAll()).ToDictionary(s => s.Id, s => s.PatternName);
            string action = "";

            Panel panel = ctx.PanelHelper.Create("Boutiques - Points placés", UIPanel.PanelType.Tab, p, async () => await ListPoints(ctx, p, kind, shopId));
            if (points.Count == 0) panel.AddTabLine("Aucun point placé", _ => { });
            foreach (NPoint point in points)
            {
                string name = names.TryGetValue(point.PatternId, out string n) ? n : Color("boutique supprimée", Red);
                panel.AddTabLine($"Point #{point.Id} - {name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            ctx.PointHelper.PlayerSetPositionToNPoint(p, point);
                            break;
                        case "move":
                            if (await ctx.PointHelper.SetNPointPosition(p, point))
                                Ok(p, "Point déplacé à votre position.");
                            break;
                        case "delete":
                            await ctx.PointHelper.DeleteNPoint(point);
                            Ok(p, $"Point #{point.Id} supprimé.");
                            panel.Refresh();
                            break;
                    }
                });
            }
            if (points.Count > 0)
            {
                panel.AddButton("Se téléporter", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton("Déplacer ici", _ => { action = "move"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Red), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }
}
