using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.ORM;
using ModKit.Utils;
using SQLite;
using UnityEngine;
using static ModKit.Helper.TextFormattingHelper;

namespace Boulangerie
{
    /// <summary>
    /// Modèle de caisse enregistreuse, lié à une entreprise. Chaque placement crée
    /// un point bleu : les employés de l'entreprise y ouvrent la caisse.
    /// </summary>
    public class CashRegister : ModEntity<CashRegister>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }

        /// <summary>Entreprise propriétaire de la caisse (reçoit l'argent des ventes).</summary>
        public int BizId { get; set; }

        /// <summary>Webhook Discord propre à cette caisse (vide = celui de la configuration).</summary>
        public string WebhookUrl { get; set; } = "";

        [Ignore]
        public string TypeName { get; set; } = nameof(CashRegister);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        private const string Tag = BoulangeriePlugin.Tag;

        private static BoulangeriePlugin Plugin => BoulangeriePlugin.Instance;

        private static BakeryConfig Config => BoulangeriePlugin.Config;

        /// <summary>Demandes de paiement en cours, par netId du client.</summary>
        private static readonly Dictionary<uint, PaymentRequest> PendingByClient = new Dictionary<uint, PaymentRequest>();

        public CashRegister() { }

        public CashRegister(bool isCreated)
        {
            TypeName = nameof(CashRegister);
        }

        // ------------------------------------------------------------------
        //  Ouverture de la caisse (point bleu)
        // ------------------------------------------------------------------

        public void OnPlayerTrigger(Player player)
        {
            // Le modèle est partagé par tous les points : on copie ses valeurs tout de suite.
            int bizId = BizId;
            string name = string.IsNullOrWhiteSpace(PatternName) ? "Caisse" : PatternName;
            string webhook = WebhookUrl;

            bool staff = BoulangeriePlugin.IsStaff(player);
            if (!staff && (!player.HasBiz || player.biz.Id != bizId))
            {
                player.Notify(Tag, $"Cette caisse appartient à {BoulangeriePlugin.BizName(bizId)}.", NotificationManager.Type.Error, 5f);
                return;
            }
            if (!staff && Config.RequireService && !player.serviceMetier)
            {
                player.Notify(Tag, "Vous devez être en service pour utiliser la caisse.", NotificationManager.Type.Warning, 5f);
                return;
            }

            int characterId = player.character.Id;
            if (!Plugin.Carts.TryGetValue(characterId, out Cart cart) || cart.BizId != bizId)
            {
                cart = new Cart { BizId = bizId };
                Plugin.Carts[characterId] = cart;
            }
            cart.RegisterName = name;
            cart.WebhookUrl = webhook;

            OpenRegister(player, cart);
        }

        /// <summary>Écran principal de la caisse : panier en cours et actions.</summary>
        public static async void OpenRegister(Player player, Cart cart)
        {
            ModKit.ModKit ctx = Plugin;
            int none = IconUtils.Others.None.Id;

            if (cart.Pending != null && (cart.Pending.Done || cart.Pending.Expired))
                cart.Pending = null;

            // Ventes du jour de ce boulanger
            long midnight = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
            int characterId = player.character.Id;
            List<BakerySale> mySales = await BakerySale.Query(s => s.CashierId == characterId && s.Date >= midnight);

            Panel panel = ctx.PanelHelper.Create($"{cart.RegisterName} - Caisse", UIPanel.PanelType.TabPrice, player, () => OpenRegister(player, cart));

            if (cart.Pending != null)
            {
                PaymentRequest req = cart.Pending;
                panel.AddTabLine(Color($"En attente du paiement de {req.ClientName}", Colors.Warning) + "<br>" + Size("Sélectionner pour annuler la demande", 14),
                    Money(req.Cart.Total), none, _ =>
                    {
                        CancelRequest(req, "Le boulanger a annulé la demande de paiement.");
                        player.Notify(Tag, "Demande de paiement annulée.", NotificationManager.Type.Info, 5f);
                        panel.Refresh();
                    });
            }

            panel.AddTabLine(Color("+ Ajouter un produit", Colors.Info), "", none, _ => ProductPicker(player, cart));
            panel.AddTabLine(Color("+ Ajouter une formule", Colors.Info), "", none, _ => FormulaPicker(player, cart));

            foreach (CartLine line in cart.Lines.ToList())
            {
                CartLine l = line;
                string label = (l.IsFormula ? Color("[Formule] ", Colors.Orange) : "") + $"{l.Quantity}x {l.Name}";
                panel.AddTabLine(label + "<br>" + Size($"{Money(l.UnitPrice)} l'unité - sélectionner pour en retirer 1", 14), Money(l.Total), none, _ =>
                {
                    cart.RemoveOne(l);
                    panel.Refresh();
                });
            }

            if (cart.IsEmpty)
                panel.AddTabLine(Color("Panier vide", Colors.Grey), "", none, _ => panel.Refresh());

            if (cart.DiscountPercent > 0)
                panel.AddTabLine($"Remise {cart.DiscountPercent}%", "-" + Money(cart.Discount), none, _ => panel.Refresh());
            panel.AddTabLine(Bold($"TOTAL ({cart.ItemCount} article(s))"), Bold(Money(cart.Total)), none, _ => panel.Refresh());

            if (!cart.IsEmpty)
            {
                panel.AddTabLine(Color("Encaisser le client", Colors.Success), "", none, _ => ChooseClient(player, cart));
                if (Config.MaxDiscountPercent > 0)
                    panel.AddTabLine("Appliquer une remise", $"{cart.DiscountPercent}%", none, _ =>
                        Plugin.AskInt(player, $"{cart.RegisterName} - Remise", "Remise en % sur tout le panier", cart.DiscountPercent, 0, Config.MaxDiscountPercent,
                            v => { cart.DiscountPercent = v; return Task.CompletedTask; }));
                panel.AddTabLine(Color("Vider le panier", Colors.Error), "", none, _ =>
                {
                    if (cart.Pending != null) CancelRequest(cart.Pending, "Le boulanger a annulé la commande.");
                    cart.Clear();
                    panel.Refresh();
                });
            }

            panel.AddTabLine(Color("Mes ventes du jour", Colors.Grey), $"{mySales.Count} / {Money(mySales.Sum(s => s.Total))}", none, _ => panel.Refresh());

            if (BoulangeriePlugin.CanManage(player, cart.BizId))
                panel.AddTabLine(Color("Gérer la boulangerie (carte, stats, historique)", Colors.Purple), "", none, _ => Plugin.ManageMenu(player, cart.BizId));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Ajout d'articles
        // ------------------------------------------------------------------

        private static async void ProductPicker(Player player, Cart cart)
        {
            int bizId = cart.BizId;
            List<BakeryProduct> products = (await BakeryProduct.Query(p => p.BizId == bizId)).Where(p => p.Available).OrderBy(p => p.Name).ToList();
            bool multiple = false;

            Panel panel = Plugin.PanelHelper.Create($"{cart.RegisterName} - Produits", UIPanel.PanelType.TabPrice, player, () => ProductPicker(player, cart));
            panel.AddTabLine(Color($"Panier : {cart.ItemCount} article(s)", Colors.Grey), Money(cart.Total), IconUtils.Others.None.Id, _ => panel.Refresh());

            if (products.Count == 0)
                panel.AddTabLine("Aucun produit en vente (Gérer la boulangerie > Produits)", "", IconUtils.Others.None.Id, _ => panel.Refresh());

            foreach (BakeryProduct product in products)
            {
                BakeryProduct p = product;
                panel.AddTabLine(p.Name, Money(p.Price), BoulangeriePlugin.Icon(p.ItemId), _ =>
                {
                    if (multiple)
                    {
                        Plugin.AskInt(player, $"{cart.RegisterName} - Quantité", $"Combien de « {p.Name} » ?", 1, 1, 100,
                            v => { cart.Add(false, p.Id, p.Name, p.Price, v); return Task.CompletedTask; });
                        return;
                    }
                    cart.Add(false, p.Id, p.Name, p.Price);
                    player.Notify(Tag, $"+1 {p.Name}", NotificationManager.Type.Success, 2f);
                    panel.Refresh();
                });
            }

            if (products.Count > 0)
            {
                panel.NextButton("Ajouter 1", () => { multiple = false; panel.SelectTab(); });
                panel.NextButton("Quantité...", () => { multiple = true; panel.SelectTab(); });
            }
            panel.PreviousButton("Panier");
            panel.CloseButton();
            panel.Display();
        }

        private static async void FormulaPicker(Player player, Cart cart)
        {
            int bizId = cart.BizId;
            List<BakeryFormula> formulas = (await BakeryFormula.Query(f => f.BizId == bizId)).Where(f => f.Available).OrderBy(f => f.Name).ToList();
            Dictionary<int, string> productNames = (await BakeryProduct.Query(p => p.BizId == bizId)).ToDictionary(p => p.Id, p => p.Name);

            Panel panel = Plugin.PanelHelper.Create($"{cart.RegisterName} - Formules", UIPanel.PanelType.TabPrice, player, () => FormulaPicker(player, cart));
            panel.AddTabLine(Color($"Panier : {cart.ItemCount} article(s)", Colors.Grey), Money(cart.Total), IconUtils.Others.None.Id, _ => panel.Refresh());

            if (formulas.Count == 0)
                panel.AddTabLine("Aucune formule (Gérer la boulangerie > Formules)", "", IconUtils.Others.None.Id, _ => panel.Refresh());

            foreach (BakeryFormula formula in formulas)
            {
                BakeryFormula f = formula;
                panel.AddTabLine(f.Name + "<br>" + Size(Color(DescribeFormula(f, productNames), Colors.Grey), 14), Money(f.Price), IconUtils.Others.None.Id, _ =>
                {
                    cart.Add(true, f.Id, f.Name, f.Price);
                    player.Notify(Tag, $"+1 formule {f.Name}", NotificationManager.Type.Success, 2f);
                    panel.Refresh();
                });
            }

            if (formulas.Count > 0)
                panel.NextButton("Ajouter", () => panel.SelectTab());
            panel.PreviousButton("Panier");
            panel.CloseButton();
            panel.Display();
        }

        public static string DescribeFormula(BakeryFormula formula, Dictionary<int, string> productNames)
        {
            Dictionary<int, int> items = formula.Items;
            if (items.Count == 0) return "vide";
            return string.Join(" + ", items.Select(i => $"{i.Value}x {(productNames.TryGetValue(i.Key, out string n) ? n : "?")}"));
        }

        // ------------------------------------------------------------------
        //  Encaissement
        // ------------------------------------------------------------------

        private static void ChooseClient(Player player, Cart cart)
        {
            if (cart.IsEmpty)
            {
                player.Notify(Tag, "Le panier est vide.", NotificationManager.Type.Warning, 5f);
                OpenRegister(player, cart);
                return;
            }

            List<Player> clients = Nova.server.GetPlayersInRange(Config.MaxClientDistance, player.setup.transform.position)
                .Where(p => p != null && p.character != null && p.netId != player.netId)
                .ToList();
            // Un staff seul peut tester la caisse sur lui-même
            if (BoulangeriePlugin.IsStaff(player)) clients.Add(player);

            Panel panel = Plugin.PanelHelper.Create($"{cart.RegisterName} - Client", UIPanel.PanelType.Tab, player, () => ChooseClient(player, cart));
            panel.TextLines.Add($"Total : {Money(cart.Total)}");

            if (clients.Count == 0)
                panel.AddTabLine($"Aucun client à moins de {Config.MaxClientDistance:0.#} m (actualiser)", _ => panel.Refresh());

            foreach (Player client in clients)
            {
                Player c = client;
                string label = c == player ? $"{c.FullName} (vous - test staff)" : c.FullName;
                panel.AddTabLine(label, _ =>
                {
                    SendPaymentRequest(player, c, cart);
                    OpenRegister(player, cart);
                });
            }

            panel.NextButton("Envoyer l'addition", () => panel.SelectTab());
            panel.PreviousButton("Panier");
            panel.CloseButton();
            panel.Display();
        }

        private static void SendPaymentRequest(Player cashier, Player client, Cart cart)
        {
            if (cart.Pending != null && !cart.Pending.Done && !cart.Pending.Expired)
            {
                cashier.Notify(Tag, $"Une demande est déjà en attente ({cart.Pending.ClientName}).", NotificationManager.Type.Warning, 5f);
                return;
            }
            if (PendingByClient.TryGetValue(client.netId, out PaymentRequest other) && !other.Done && !other.Expired)
            {
                cashier.Notify(Tag, $"{client.FullName} a déjà une addition en attente.", NotificationManager.Type.Warning, 5f);
                return;
            }

            PaymentRequest req = new PaymentRequest
            {
                Cart = cart.Snapshot(),
                CashierCharacterId = cashier.character.Id,
                CashierNetId = cashier.netId,
                ClientNetId = client.netId,
                ClientName = client.FullName,
                ExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(10, Config.PaymentTimeout))
            };
            cart.Pending = req;
            PendingByClient[client.netId] = req;

            ShowPaymentPanel(client, req, cashier.FullName);
            cashier.Notify(Tag, $"Addition de {Money(req.Cart.Total)} envoyée à {client.FullName}.", NotificationManager.Type.Info, 5f);
        }

        /// <summary>Panel affiché au client : récapitulatif et choix du moyen de paiement.</summary>
        private static void ShowPaymentPanel(Player client, PaymentRequest req, string cashierName)
        {
            Cart c = req.Cart;
            Panel panel = Plugin.PanelHelper.Create($"{c.RegisterName} - Addition", UIPanel.PanelType.Text, client, () => ShowPaymentPanel(client, req, cashierName));

            panel.TextLines.Add(Bold(BoulangeriePlugin.BizName(c.BizId)));
            panel.TextLines.Add(Color($"Servi par {cashierName}", Colors.Grey));
            panel.TextLines.Add("");
            foreach (CartLine line in c.Lines)
                panel.TextLines.Add($"{line.Quantity}x {(line.IsFormula ? "Formule " : "")}{line.Name} : {Money(line.Total)}");
            panel.TextLines.Add("");
            if (c.DiscountPercent > 0)
            {
                panel.TextLines.Add($"Sous-total : {Money(c.SubTotal)}");
                panel.TextLines.Add(Color($"Remise {c.DiscountPercent}% : -{Money(c.Discount)}", Colors.Success));
            }
            panel.TextLines.Add(Size(Bold($"Total à payer : {Money(c.Total)}"), 22));
            panel.TextLines.Add(Color($"Espèces : {Money(client.Money)}  |  Banque : {Money(client.Bank)}", Colors.Grey));

            panel.CloseButtonWithAction(Color("Espèces", Colors.Success), async () => await Pay(client, req, true));
            panel.CloseButtonWithAction(Color("Carte bancaire", Colors.Info), async () => await Pay(client, req, false));
            panel.CloseButtonWithAction(Color("Refuser", Colors.Error), () =>
            {
                Decline(client, req);
                return Task.FromResult(true);
            });
            req.ClientPanel = panel;
            panel.Display();
        }

        private static void Decline(Player client, PaymentRequest req)
        {
            if (req.Done) return;
            Finish(req);
            BoulangeriePlugin.FindPlayer(req.CashierNetId)?.Notify(Tag, $"{client.FullName} a refusé de payer.", NotificationManager.Type.Warning, 5f);
            client.Notify(Tag, "Vous avez refusé l'addition.", NotificationManager.Type.Info, 5f);
        }

        private static void CancelRequest(PaymentRequest req, string message)
        {
            if (req.Done) return;
            Finish(req);
            req.ClientPanel?.Close();
            Player client = BoulangeriePlugin.FindPlayer(req.ClientNetId);
            client?.Notify(Tag, message, NotificationManager.Type.Info, 5f);
        }

        /// <summary>Clôt la demande et la détache du panier du boulanger.</summary>
        private static void Finish(PaymentRequest req)
        {
            req.Done = true;
            if (PendingByClient.TryGetValue(req.ClientNetId, out PaymentRequest current) && current == req)
                PendingByClient.Remove(req.ClientNetId);
            if (Plugin.Carts.TryGetValue(req.CashierCharacterId, out Cart cart) && cart.Pending == req)
                cart.Pending = null;
        }

        /// <summary>Paiement par le client. Renvoie vrai pour fermer le panel.</summary>
        private static async Task<bool> Pay(Player client, PaymentRequest req, bool cash)
        {
            if (req.Done)
            {
                client.Notify(Tag, "Cette addition n'est plus valable.", NotificationManager.Type.Error, 5f);
                return true;
            }
            Player cashier = BoulangeriePlugin.FindPlayer(req.CashierNetId);
            if (req.Expired || cashier == null)
            {
                Finish(req);
                client.Notify(Tag, "Cette addition a expiré.", NotificationManager.Type.Error, 5f);
                cashier?.Notify(Tag, $"L'addition de {client.FullName} a expiré.", NotificationManager.Type.Warning, 5f);
                return true;
            }

            Cart c = req.Cart;
            double total = c.Total;
            string method = cash ? "Espèces" : "Carte bancaire";

            if (cash && client.Money < total)
            {
                client.Notify(Tag, $"Vous n'avez pas assez d'espèces ({Money(client.Money)} / {Money(total)}).", NotificationManager.Type.Error, 5f);
                return false;
            }
            if (!cash && client.Bank < total)
            {
                client.Notify(Tag, $"Solde bancaire insuffisant ({Money(client.Bank)} / {Money(total)}).", NotificationManager.Type.Error, 5f);
                return false;
            }

            // Demande clôturée avant tout await : un double clic ne peut pas faire payer deux fois.
            Finish(req);

            // Items à remettre au client
            Dictionary<int, int> items = Config.GiveItems ? await ItemsToGive(c) : new Dictionary<int, int>();
            if (Config.UseCashierStock)
            {
                List<string> missing = items
                    .Where(i => !InventoryUtils.CheckInventoryContainsItem(cashier, i.Key, i.Value))
                    .Select(i => $"{i.Value}x {BoulangeriePlugin.ItemName(i.Key)}")
                    .ToList();
                if (missing.Count > 0)
                {
                    cashier.Notify(Tag, "Stock insuffisant dans votre inventaire : " + string.Join(", ", missing), NotificationManager.Type.Error, 8f);
                    client.Notify(Tag, "Le boulanger n'a pas tous les produits, la vente est annulée.", NotificationManager.Type.Warning, 5f);
                    return true;
                }
            }

            // Paiement
            if (cash)
                client.AddMoney(-total, "Boulangerie");
            else
                client.AddBankMoney(-total, "Boulangerie");

            double commission = Math.Round(total * Math.Max(0, Math.Min(100, Config.CommissionPercent)) / 100.0, 2);
            Bizs biz = Nova.biz.FetchBiz(c.BizId);
            if (biz != null)
                biz.AddBankMoney(total - commission, $"Vente boulangerie ({client.FullName})");
            else
                commission = total; // entreprise supprimée : tout revient au boulanger
            if (commission > 0)
                cashier.AddBankMoney(commission, "Commission boulangerie");

            // Remise des items
            List<string> notGiven = new List<string>();
            foreach (KeyValuePair<int, int> item in items)
            {
                if (Config.UseCashierStock)
                    InventoryUtils.RemoveFromInventory(cashier, item.Key, item.Value);
                if (!InventoryUtils.AddItem(client, item.Key, item.Value))
                {
                    notGiven.Add($"{item.Value}x {BoulangeriePlugin.ItemName(item.Key)}");
                    if (Config.UseCashierStock) InventoryUtils.AddItem(cashier, item.Key, item.Value);
                }
            }

            // Vidage du panier du boulanger s'il n'a pas été modifié depuis
            if (Plugin.Carts.TryGetValue(req.CashierCharacterId, out Cart cashierCart) && SameContent(cashierCart, c))
                cashierCart.Clear();

            string details = string.Join("\n", c.Lines.Select(l => $"{l.Quantity}x {(l.IsFormula ? "Formule " : "")}{l.Name} : {Money(l.Total)}"));
            BakerySale sale = new BakerySale
            {
                BizId = c.BizId,
                RegisterName = c.RegisterName,
                CashierId = cashier.character.Id,
                CashierName = cashier.FullName,
                ClientId = client.character.Id,
                ClientName = client.FullName,
                Total = total,
                DiscountPercent = c.DiscountPercent,
                PaymentMethod = method,
                Details = details,
                Date = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            await sale.Save();

            client.Notify(Tag, $"Merci ! Vous avez payé {Money(total)} ({method}). Bonne dégustation !", NotificationManager.Type.Success, 6f);
            cashier.Notify(Tag, $"Paiement reçu : {Money(total)} de {client.FullName} ({method})."
                + (commission > 0 ? $" Commission : {Money(commission)}." : ""), NotificationManager.Type.Success, 6f);
            if (notGiven.Count > 0)
            {
                string list = string.Join(", ", notGiven);
                client.Notify(Tag, "Inventaire plein, non reçu : " + list, NotificationManager.Type.Warning, 8f);
                cashier.Notify(Tag, $"{client.FullName} n'a pas pu recevoir : {list}. Remettez-les-lui à la main.", NotificationManager.Type.Warning, 8f);
            }

            BoulangeriePlugin.SendWebhook(c.WebhookUrl, "🥐 Nouvelle vente", details,
                new List<string> { "Boulangerie", "Caisse", "Vendeur", "Client", "Paiement", "Total", "Remise", "Ticket" },
                new List<string>
                {
                    BoulangeriePlugin.BizName(c.BizId), c.RegisterName, cashier.FullName, client.FullName, method,
                    Money(total), c.DiscountPercent > 0 ? $"{c.DiscountPercent}% (-{Money(c.Discount)})" : "Aucune", $"#{sale.Id}"
                });
            return true;
        }

        private static bool SameContent(Cart a, Cart b)
        {
            if (a.Lines.Count != b.Lines.Count || a.DiscountPercent != b.DiscountPercent) return false;
            return a.Lines.All(l => b.Lines.Any(o => o.IsFormula == l.IsFormula && o.RefId == l.RefId && o.Quantity == l.Quantity));
        }

        /// <summary>Items de jeu correspondant au panier (produits et contenu des formules).</summary>
        private static async Task<Dictionary<int, int>> ItemsToGive(Cart cart)
        {
            int bizId = cart.BizId;
            Dictionary<int, BakeryProduct> products = (await BakeryProduct.Query(p => p.BizId == bizId)).ToDictionary(p => p.Id);
            Dictionary<int, BakeryFormula> formulas = (await BakeryFormula.Query(f => f.BizId == bizId)).ToDictionary(f => f.Id);
            Dictionary<int, int> items = new Dictionary<int, int>();

            void AddProduct(int productId, int quantity)
            {
                if (!products.TryGetValue(productId, out BakeryProduct product) || product.ItemId <= 0) return;
                items[product.ItemId] = (items.TryGetValue(product.ItemId, out int q) ? q : 0) + quantity;
            }

            foreach (CartLine line in cart.Lines)
            {
                if (!line.IsFormula)
                    AddProduct(line.RefId, line.Quantity);
                else if (formulas.TryGetValue(line.RefId, out BakeryFormula formula))
                    foreach (KeyValuePair<int, int> content in formula.Items)
                        AddProduct(content.Key, content.Value * line.Quantity);
            }
            return items;
        }

        private static string Money(double amount) => BoulangeriePlugin.Money(amount);

        // ------------------------------------------------------------------
        //  Côté staff : modèles de caisse et points (AAMenu > Points / /caisses)
        // ------------------------------------------------------------------

        public async Task SetProperties(int id)
        {
            CashRegister result = await Query(id);
            Id = id;
            TypeName = nameof(CashRegister);
            PatternName = result?.PatternName;
            BizId = result?.BizId ?? 0;
            WebhookUrl = result?.WebhookUrl;
        }

        /// <summary>Menu staff : choisir un modèle de caisse et la placer à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!BoulangeriePlugin.IsStaff(player))
            {
                player.Notify(Tag, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                return;
            }

            List<CashRegister> patterns = await QueryAll();
            Panel panel = Context.PanelHelper.Create("Caisses - Placer une caisse", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle, créez-en un", _ => panel.Refresh());
            foreach (CashRegister pattern in patterns)
            {
                CashRegister p = pattern;
                panel.AddTabLine($"[{p.Id}] {p.PatternName} - {BoulangeriePlugin.BizName(p.BizId)}", async _ =>
                {
                    p.TypeName = nameof(CashRegister);
                    p.Context = Context;
                    if (await Context.PointHelper.CreateNPoint(player, p))
                        player.Notify(Tag, $"Caisse « {p.PatternName} » placée à votre position.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify(Tag, "Erreur lors de la création du point.", NotificationManager.Type.Error, 5f);
                });
            }

            if (patterns.Count > 0)
                panel.AddButton("Placer ici", _ => panel.SelectTab());
            panel.NextButton("Nouveau modèle", () => SetPatternData(player));
            panel.NextButton("Modèles", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Création d'un modèle : nom puis entreprise (même titre = une seule entrée « Retour »).</summary>
        public void SetPatternData(Player player)
        {
            const string title = "Caisses - Nouveau modèle";
            Panel panel = Context.PanelHelper.Create(title, UIPanel.PanelType.Input, player, () => SetPatternData(player));
            panel.TextLines.Add("Étape 1/2 : nom de la caisse (ex. « Caisse comptoir »)");
            panel.SetInputPlaceholder("Caisse");

            panel.AddButton("Suivant", _ =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    player.Notify(Tag, "Le nom ne peut pas être vide.", NotificationManager.Type.Error, 5f);
                    return;
                }
                player.ClosePanel(panel);
                Plugin.ChooseBiz(player, title, null, async (bizPanel, biz) =>
                {
                    CashRegister pattern = new CashRegister(false) { PatternName = name, BizId = biz.Id };
                    if (await pattern.Save())
                        player.Notify(Tag, $"Caisse « {name} » créée pour {BoulangeriePlugin.BizName(biz.Id)}. Choisissez-la puis « Placer ici ».", NotificationManager.Type.Success, 6f);
                    else
                        player.Notify(Tag, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                    bizPanel.Previous();
                });
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste des modèles : renommer, changer d'entreprise, webhook, supprimer.</summary>
        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<CashRegister> patterns = await QueryAll();
            string action = "";

            Panel panel = Context.PanelHelper.Create("Caisses - Modèles", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle", _ => panel.Refresh());
            foreach (CashRegister pattern in patterns)
            {
                CashRegister p = pattern;
                string hook = string.IsNullOrWhiteSpace(p.WebhookUrl) ? "" : " - webhook perso";
                panel.AddTabLine($"[{p.Id}] {p.PatternName} - {BoulangeriePlugin.BizName(p.BizId)}{hook}", async _ =>
                {
                    p.TypeName = nameof(CashRegister);
                    p.Context = Context;
                    switch (action)
                    {
                        case "rename":
                            Plugin.AskInput(player, "Caisses - Modifier", $"Nouveau nom pour « {p.PatternName} »", p.PatternName, async input =>
                            {
                                if (input.Length == 0) return false;
                                p.PatternName = input;
                                return await p.Save();
                            });
                            break;
                        case "biz":
                            Plugin.ChooseBiz(player, "Caisses - Entreprise", null, async (bizPanel, biz) =>
                            {
                                p.BizId = biz.Id;
                                await p.Save();
                                player.Notify(Tag, $"« {p.PatternName} » appartient maintenant à {BoulangeriePlugin.BizName(biz.Id)}.", NotificationManager.Type.Success, 5f);
                                bizPanel.Previous();
                            });
                            break;
                        case "webhook":
                            Plugin.AskInput(player, "Caisses - Modifier", "Webhook Discord propre à cette caisse (vide = webhook de la configuration)", p.WebhookUrl, async input =>
                            {
                                if (input.Length > 0 && !input.StartsWith("https://"))
                                {
                                    player.Notify(Tag, "L'URL doit commencer par https://", NotificationManager.Type.Error, 5f);
                                    return false;
                                }
                                p.WebhookUrl = input;
                                return await p.Save();
                            });
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPointsByPattern(player, p);
                            if (await p.Delete())
                                player.Notify(Tag, $"Modèle « {p.PatternName} » et ses caisses supprimés.", NotificationManager.Type.Success, 5f);
                            else
                                player.Notify(Tag, "Erreur lors de la suppression.", NotificationManager.Type.Error, 5f);
                            panel.Refresh();
                            break;
                        default:
                            panel.Refresh();
                            break;
                    }
                });
            }

            if (patterns.Count > 0 && forEdit)
            {
                panel.NextButton("Renommer", () => { action = "rename"; panel.SelectTab(); });
                panel.NextButton("Entreprise", () => { action = "biz"; panel.SelectTab(); });
                panel.NextButton("Webhook", () => { action = "webhook"; panel.SelectTab(); });
                panel.NextButton(Color("Supprimer", Colors.Error), () => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste de toutes les caisses placées : se téléporter, déplacer ou supprimer.</summary>
        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(CashRegister));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Context.PanelHelper.Create("Caisses - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));

            if (points.Count == 0)
                panel.AddTabLine("Aucune caisse placée", _ => { });
            foreach (NPoint point in points)
            {
                NPoint pt = point;
                string name = names.TryGetValue(pt.PatternId, out string n) ? n : "?";
                panel.AddTabLine($"Point #{pt.Id} - {name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            Context.PointHelper.PlayerSetPositionToNPoint(player, pt);
                            break;
                        case "move":
                            if (await Context.PointHelper.SetNPointPosition(player, pt))
                                player.Notify(Tag, "Caisse déplacée à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPoint(pt);
                            player.Notify(Tag, $"Caisse #{pt.Id} supprimée.", NotificationManager.Type.Success, 5f);
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
}
