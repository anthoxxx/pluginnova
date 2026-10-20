using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Life;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using ModKit.Utils;
using static ModKit.Helper.TextFormattingHelper;

namespace Boulangerie
{
    /// <summary>Gestion de la carte (produits, formules), statistiques et historique des ventes.</summary>
    public partial class BoulangeriePlugin
    {
        private const string EditTitle = "Boulangerie - Modifier";

        /// <summary>Menu de gestion d'une boulangerie (patron / staff).</summary>
        public async void ManageMenu(Player player, int bizId, bool fromAAMenu = false)
        {
            if (!CanManage(player, bizId))
            {
                player.Notify(Tag, "Vous n'avez pas la permission de gérer cette boulangerie.", NotificationManager.Type.Error, 5f);
                return;
            }

            int products = (await BakeryProduct.Query(p => p.BizId == bizId)).Count;
            int formulas = (await BakeryFormula.Query(f => f.BizId == bizId)).Count;

            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand("Boulangerie - Gestion"), UIPanel.PanelType.Tab, player, () => ManageMenu(player, bizId, fromAAMenu));
            panel.AddTabLine($"Produits ({products})", _ => ProductsList(player, bizId));
            panel.AddTabLine($"Formules maison ({formulas})", _ => FormulasList(player, bizId));
            panel.AddTabLine("Stock / réapprovisionner", _ => StockPanel(player, bizId));
            panel.AddTabLine("Voir la carte client", _ => MenuCard.Show(player, bizId, true));
            panel.AddTabLine("Statistiques", _ => Stats(player, bizId));
            panel.AddTabLine("Historique des ventes", _ => History(player, bizId));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            if (fromAAMenu && AAMenu.AAMenu.menu != null)
                panel.AddButton("Retour", _ => { panel.Close(); AAMenu.AAMenu.menu.BizPanel(player); });
            else
                panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Produits
        // ------------------------------------------------------------------

        private async void ProductsList(Player player, int bizId)
        {
            List<BakeryProduct> products = (await BakeryProduct.Query(p => p.BizId == bizId)).OrderBy(p => p.Name).ToList();

            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand("Boulangerie - Produits"), UIPanel.PanelType.TabPrice, player, () => ProductsList(player, bizId));
            if (products.Count == 0)
                panel.AddTabLine("Aucun produit : cliquez sur « Ajouter »", "", DefaultIcon, _ => panel.Refresh());
            foreach (BakeryProduct product in products)
            {
                BakeryProduct p = product;
                panel.AddTabLine(p.Name + "<br>" + Size(StockHelper.Label(p) + Color(" - Item : " + ItemName(p.ItemId), Colors.Grey), 14),
                    Money(p.Price), ProductIcon(p), _ => ProductEdit(player, p));
            }

            if (products.Count > 0)
                panel.NextButton("Modifier", () => panel.SelectTab());
            panel.NextButton("Ajouter", () => NewProductName(player, new BakeryProduct { BizId = bizId }));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // Assistant de création : toutes les étapes ont le même titre, « Retour » ramène donc à la liste.
        private const string NewProductTitle = "Boulangerie - Nouveau produit";

        private void NewProductName(Player player, BakeryProduct product)
        {
            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand(NewProductTitle), UIPanel.PanelType.Input, player, () => NewProductName(player, product));
            panel.TextLines.Add("Étape 1/3 : nom du produit (ex. Croissant, Baguette, Café...)");
            panel.SetInputPlaceholder(product.Name ?? "Croissant");
            panel.AddButton("Suivant", _ =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    player.Notify(Tag, "Le nom ne peut pas être vide.", NotificationManager.Type.Error, 5f);
                    return;
                }
                product.Name = name;
                player.ClosePanel(panel);
                NewProductPrice(player, product);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void NewProductPrice(Player player, BakeryProduct product)
        {
            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand(NewProductTitle), UIPanel.PanelType.Input, player, () => NewProductPrice(player, product));
            panel.TextLines.Add($"Étape 2/3 : prix de vente de « {product.Name} » (ex. 1.50)");
            panel.SetInputPlaceholder("1.50");
            panel.AddButton("Suivant", _ =>
            {
                if (!TryParseAmount(panel.inputText, out double price))
                {
                    player.Notify(Tag, "Prix invalide.", NotificationManager.Type.Error, 5f);
                    return;
                }
                product.Price = Math.Round(price, 2);
                player.ClosePanel(panel);
                ChooseItem(player, NewProductTitle, $"Étape 3/3 : item remis au client pour « {product.Name} »", async (current, itemId) =>
                {
                    product.ItemId = itemId;
                    if (await product.Save())
                        player.Notify(Tag, $"Produit « {product.Name} » ajouté à {Money(product.Price)}.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify(Tag, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                    current.Previous();
                });
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>
        /// Choix de l'item du jeu lié à un produit : dans l'inventaire du joueur, aucun, ou par ID.
        /// <paramref name="onChosen"/> reçoit le panel courant (à fermer avec Previous()).
        /// </summary>
        private void ChooseItem(Player player, string title, string text, Func<Panel, int, Task> onChosen)
        {
            Dictionary<int, int> inventory = InventoryUtils.ReturnPlayerInventory(player);

            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand(title), UIPanel.PanelType.TabPrice, player, () => ChooseItem(player, title, text, onChosen));
            panel.AddTabLine(Color(text, Colors.Grey), "", DefaultIcon, _ => { });
            panel.AddTabLine("Aucun item (prestation simple)", "", DefaultIcon, async _ => await onChosen(panel, 0));
            panel.AddTabLine(Color("Saisir l'ID d'un item...", Colors.Info), "", DefaultIcon, _ =>
            {
                player.ClosePanel(panel);
                ItemIdInput(player, title, onChosen);
            });
            foreach (int itemId in inventory.Keys)
            {
                int id = itemId;
                panel.AddTabLine(ItemName(id), $"ID {id}", Icon(id), async _ => await onChosen(panel, id));
            }
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void ItemIdInput(Player player, string title, Func<Panel, int, Task> onChosen)
        {
            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand(title), UIPanel.PanelType.Input, player, () => ItemIdInput(player, title, onChosen));
            panel.TextLines.Add("ID de l'item du jeu (0 = aucun item)");
            panel.SetInputPlaceholder("0");
            panel.AddButton("Valider", async _ =>
            {
                if (!int.TryParse(panel.inputText?.Trim(), out int id) || id < 0)
                {
                    player.Notify(Tag, "ID invalide.", NotificationManager.Type.Error, 5f);
                    return;
                }
                if (id > 0 && ItemUtils.GetItemById(id) == null)
                {
                    player.Notify(Tag, $"Aucun item avec l'ID {id}.", NotificationManager.Type.Error, 5f);
                    return;
                }
                await onChosen(panel, id);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void ProductEdit(Player player, BakeryProduct product)
        {
            int none = DefaultIcon;
            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand("Boulangerie - Produit"), UIPanel.PanelType.TabPrice, player, () => ProductEdit(player, product));

            panel.AddTabLine("Nom", product.Name, none, _ =>
                AskInput(player, EditTitle, "Nouveau nom du produit", product.Name, async input =>
                {
                    if (input.Length == 0) return false;
                    product.Name = input;
                    return await product.Save();
                }));
            panel.AddTabLine("Prix", Money(product.Price), none, _ =>
                AskInput(player, EditTitle, $"Nouveau prix de « {product.Name} »", product.Price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), async input =>
                {
                    if (!TryParseAmount(input, out double price))
                    {
                        player.Notify(Tag, "Prix invalide.", NotificationManager.Type.Error, 5f);
                        return false;
                    }
                    product.Price = Math.Round(price, 2);
                    return await product.Save();
                }));
            panel.AddTabLine("Item remis au client", ItemName(product.ItemId), Icon(product.ItemId), _ =>
                ChooseItem(player, "Boulangerie - Item lié", $"Item remis pour « {product.Name} »", async (current, itemId) =>
                {
                    product.ItemId = itemId;
                    await product.Save();
                    current.Previous();
                }));
            panel.AddTabLine("Icône sur les panels", IconLabel(product.IconId, "Auto (icône de l'item)"), ProductIcon(product), _ =>
                IconPicker(player, "Boulangerie - Icône du produit", "Auto (icône de l'item lié)", 0, async icon =>
                {
                    product.IconId = icon;
                    await product.Save();
                }));
            panel.AddTabLine("En vente", OnOff(product.Available), none, async _ =>
            {
                product.Available = !product.Available;
                await product.Save();
                panel.Refresh();
            });
            panel.AddTabLine("Suivi du stock", product.TrackStock ? Color("Oui", Colors.Success) : Color("Non (illimité)", Colors.Grey), none, async _ =>
            {
                product.TrackStock = !product.TrackStock;
                await product.Save();
                panel.Refresh();
            });
            if (product.TrackStock)
                panel.AddTabLine("Quantité en stock", product.Stock.ToString(), none, _ =>
                    AskInt(player, EditTitle, $"Quantité de « {product.Name} » en stock", product.Stock, 0, 100000, async v =>
                    {
                        product.Stock = v;
                        await product.Save();
                    }));
            panel.AddTabLine(Color("Supprimer le produit", Colors.Error), "", none, async _ =>
            {
                // Retire aussi le produit des formules qui le contiennent
                int bizId = product.BizId;
                foreach (BakeryFormula formula in await BakeryFormula.Query(f => f.BizId == bizId))
                {
                    Dictionary<int, int> items = formula.Items;
                    if (items.Remove(product.Id))
                    {
                        formula.Items = items;
                        await formula.Save();
                    }
                }
                await product.Delete();
                player.Notify(Tag, $"Produit « {product.Name} » supprimé.", NotificationManager.Type.Success, 5f);
                panel.Previous();
            });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Réapprovisionnement rapide : accessible à tous les employés de la boulangerie.</summary>
        public async void StockPanel(Player player, int bizId)
        {
            if (!IsStaff(player) && (!player.HasBiz || player.biz.Id != bizId))
            {
                player.Notify(Tag, "Vous ne travaillez pas dans cette boulangerie.", NotificationManager.Type.Error, 5f);
                return;
            }
            bool canManage = CanManage(player, bizId);
            List<BakeryProduct> products = (await BakeryProduct.Query(p => p.BizId == bizId)).OrderBy(p => p.Name).ToList();
            string action = "add";

            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand("Boulangerie - Stock"), UIPanel.PanelType.TabPrice, player, () => StockPanel(player, bizId));
            if (products.Count == 0)
                panel.AddTabLine("Aucun produit", "", DefaultIcon, _ => panel.Refresh());
            foreach (BakeryProduct product in products)
            {
                BakeryProduct p = product;
                string qty = p.TrackStock ? $"x{p.Stock}" : Color("illimité", Colors.Grey);
                panel.AddTabLine(p.Name + "<br>" + Size(StockHelper.Label(p), 14), qty, ProductIcon(p), async _ =>
                {
                    switch (action)
                    {
                        case "add":
                            AskInt(player, EditTitle, $"Combien de « {p.Name} » ajouter au stock ?", 10, 1, 10000, async v =>
                            {
                                p.TrackStock = true;
                                p.Stock += v;
                                await p.Save();
                                player.Notify(Tag, $"+{v} {p.Name} (stock : {p.Stock}).", NotificationManager.Type.Success, 4f);
                            });
                            break;
                        case "set":
                            AskInt(player, EditTitle, $"Nouvelle quantité de « {p.Name} » en stock", p.Stock, 0, 100000, async v =>
                            {
                                p.TrackStock = true;
                                p.Stock = v;
                                await p.Save();
                            });
                            break;
                        case "toggle":
                            if (canManage)
                            {
                                p.TrackStock = !p.TrackStock;
                                await p.Save();
                            }
                            panel.Refresh();
                            break;
                    }
                });
            }
            if (products.Count > 0)
            {
                panel.NextButton("Ajouter au stock", () => { action = "add"; panel.SelectTab(); });
                panel.NextButton("Définir", () => { action = "set"; panel.SelectTab(); });
                if (canManage)
                    panel.NextButton("Illimité / suivi", () => { action = "toggle"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Formules
        // ------------------------------------------------------------------

        private async void FormulasList(Player player, int bizId)
        {
            List<BakeryFormula> formulas = (await BakeryFormula.Query(f => f.BizId == bizId)).OrderBy(f => f.Name).ToList();
            Dictionary<int, string> names = (await BakeryProduct.Query(p => p.BizId == bizId)).ToDictionary(p => p.Id, p => p.Name);

            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand("Boulangerie - Formules"), UIPanel.PanelType.TabPrice, player, () => FormulasList(player, bizId));
            if (formulas.Count == 0)
                panel.AddTabLine("Aucune formule : cliquez sur « Ajouter »", "", DefaultIcon, _ => panel.Refresh());
            foreach (BakeryFormula formula in formulas)
            {
                BakeryFormula f = formula;
                string state = f.Available ? "" : Color(" (indisponible)", Colors.Error);
                panel.AddTabLine(f.Name + state + "<br>" + Size(Color(CashRegister.DescribeFormula(f, names), Colors.Grey), 14),
                    Money(f.Price), FormulaIcon(f), _ => FormulaEdit(player, f));
            }

            if (formulas.Count > 0)
                panel.NextButton("Modifier", () => panel.SelectTab());
            panel.NextButton("Ajouter", () => NewFormulaName(player, new BakeryFormula { BizId = bizId }));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private const string NewFormulaTitle = "Boulangerie - Nouvelle formule";

        private void NewFormulaName(Player player, BakeryFormula formula)
        {
            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand(NewFormulaTitle), UIPanel.PanelType.Input, player, () => NewFormulaName(player, formula));
            panel.TextLines.Add("Étape 1/3 : nom de la formule (ex. Petit-déjeuner)");
            panel.SetInputPlaceholder(formula.Name ?? "Petit-déjeuner");
            panel.AddButton("Suivant", _ =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    player.Notify(Tag, "Le nom ne peut pas être vide.", NotificationManager.Type.Error, 5f);
                    return;
                }
                formula.Name = name;
                player.ClosePanel(panel);
                NewFormulaPrice(player, formula);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void NewFormulaPrice(Player player, BakeryFormula formula)
        {
            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand(NewFormulaTitle), UIPanel.PanelType.Input, player, () => NewFormulaPrice(player, formula));
            panel.TextLines.Add($"Étape 2/3 : prix de la formule « {formula.Name} » (ex. 3.50)");
            panel.SetInputPlaceholder("3.50");
            panel.AddButton("Suivant", _ =>
            {
                if (!TryParseAmount(panel.inputText, out double price))
                {
                    player.Notify(Tag, "Prix invalide.", NotificationManager.Type.Error, 5f);
                    return;
                }
                formula.Price = Math.Round(price, 2);
                player.ClosePanel(panel);
                FormulaContent(player, formula, NewFormulaTitle, true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Choix des produits contenus dans une formule (+1 / -1).</summary>
        private async void FormulaContent(Player player, BakeryFormula formula, string title, bool creating)
        {
            int bizId = formula.BizId;
            List<BakeryProduct> products = (await BakeryProduct.Query(p => p.BizId == bizId)).OrderBy(p => p.Name).ToList();
            Dictionary<int, int> items = formula.Items;
            bool remove = false;

            double separate = products.Where(p => items.ContainsKey(p.Id)).Sum(p => p.Price * items[p.Id]);

            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand(title), UIPanel.PanelType.TabPrice, player, () => FormulaContent(player, formula, title, creating));
            string header = creating ? "Étape 3/3 : contenu de la formule" : "Contenu de la formule";
            panel.AddTabLine(Color($"{header} « {formula.Name} »", Colors.Grey) + "<br>" +
                Size($"Prix formule {Money(formula.Price)} - à l'unité {Money(separate)}", 14), "", DefaultIcon, _ => panel.Refresh());

            if (products.Count == 0)
                panel.AddTabLine(Color("Créez d'abord des produits", Colors.Error), "", DefaultIcon, _ => panel.Refresh());

            foreach (BakeryProduct product in products)
            {
                BakeryProduct p = product;
                string qty = items.TryGetValue(p.Id, out int q) ? Color($"x{q}", Colors.Success) : "-";
                panel.AddTabLine(p.Name, qty, ProductIcon(p), async _ =>
                {
                    Dictionary<int, int> content = formula.Items;
                    int current = content.TryGetValue(p.Id, out int c) ? c : 0;
                    current = remove ? current - 1 : current + 1;
                    if (current <= 0) content.Remove(p.Id); else content[p.Id] = current;
                    formula.Items = content;
                    if (!creating) await formula.Save();
                    panel.Refresh();
                });
            }

            if (products.Count > 0)
            {
                panel.NextButton("+1", () => { remove = false; panel.SelectTab(); });
                panel.NextButton("-1", () => { remove = true; panel.SelectTab(); });
            }
            if (creating)
            {
                panel.PreviousButtonWithAction(Color("Créer la formule", Colors.Success), async () =>
                {
                    if (formula.Items.Count == 0)
                    {
                        player.Notify(Tag, "Ajoutez au moins un produit à la formule.", NotificationManager.Type.Error, 5f);
                        return false;
                    }
                    if (!await formula.Save())
                    {
                        player.Notify(Tag, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                        return false;
                    }
                    player.Notify(Tag, $"Formule « {formula.Name} » créée à {Money(formula.Price)}.", NotificationManager.Type.Success, 5f);
                    return true;
                });
                panel.PreviousButton();
            }
            else
            {
                panel.PreviousButton();
            }
            panel.CloseButton();
            panel.Display();
        }

        private async void FormulaEdit(Player player, BakeryFormula formula)
        {
            int none = DefaultIcon;
            int bizId = formula.BizId;
            Dictionary<int, string> names = (await BakeryProduct.Query(p => p.BizId == bizId)).ToDictionary(p => p.Id, p => p.Name);

            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand("Boulangerie - Formule"), UIPanel.PanelType.TabPrice, player, () => FormulaEdit(player, formula));

            panel.AddTabLine("Nom", formula.Name, none, _ =>
                AskInput(player, EditTitle, "Nouveau nom de la formule", formula.Name, async input =>
                {
                    if (input.Length == 0) return false;
                    formula.Name = input;
                    return await formula.Save();
                }));
            panel.AddTabLine("Prix", Money(formula.Price), none, _ =>
                AskInput(player, EditTitle, $"Nouveau prix de « {formula.Name} »", formula.Price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), async input =>
                {
                    if (!TryParseAmount(input, out double price))
                    {
                        player.Notify(Tag, "Prix invalide.", NotificationManager.Type.Error, 5f);
                        return false;
                    }
                    formula.Price = Math.Round(price, 2);
                    return await formula.Save();
                }));
            panel.AddTabLine("Contenu<br>" + Size(Color(CashRegister.DescribeFormula(formula, names), Colors.Grey), 14), "", none, _ =>
                FormulaContent(player, formula, "Boulangerie - Contenu formule", false));
            panel.AddTabLine("Icône sur les panels", IconLabel(formula.IconId, "Par défaut"), FormulaIcon(formula), _ =>
                IconPicker(player, "Boulangerie - Icône de la formule", "Icône par défaut des formules", 0, async icon =>
                {
                    formula.IconId = icon;
                    await formula.Save();
                }));
            panel.AddTabLine("En vente", OnOff(formula.Available), none, async _ =>
            {
                formula.Available = !formula.Available;
                await formula.Save();
                panel.Refresh();
            });
            panel.AddTabLine(Color("Supprimer la formule", Colors.Error), "", none, async _ =>
            {
                await formula.Delete();
                player.Notify(Tag, $"Formule « {formula.Name} » supprimée.", NotificationManager.Type.Success, 5f);
                panel.Previous();
            });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Statistiques et historique
        // ------------------------------------------------------------------

        private static readonly Regex DetailLine = new Regex(@"^(\d+)x (.+) : ", RegexOptions.Compiled);

        private async void Stats(Player player, int bizId)
        {
            List<BakerySale> sales = await BakerySale.Query(s => s.BizId == bizId);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long midnight = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();

            string Period(string label, IEnumerable<BakerySale> list)
            {
                List<BakerySale> l = list.ToList();
                return $"{label} : {Bold(Money(l.Sum(s => s.Total)))} ({l.Count} vente(s))";
            }

            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand("Boulangerie - Statistiques"), UIPanel.PanelType.Text, player, () => Stats(player, bizId));
            panel.TextLines.Add(Bold(BizName(bizId)));
            panel.TextLines.Add(Period("Aujourd'hui", sales.Where(s => s.Date >= midnight)));
            panel.TextLines.Add(Period("7 derniers jours", sales.Where(s => s.Date >= now - 7 * 86400)));
            panel.TextLines.Add(Period("30 derniers jours", sales.Where(s => s.Date >= now - 30 * 86400)));
            panel.TextLines.Add(Period("Depuis le début", sales));
            panel.TextLines.Add($"Espèces : {Money(sales.Where(s => s.PaymentMethod == "Espèces").Sum(s => s.Total))}  |  Carte : {Money(sales.Where(s => s.PaymentMethod != "Espèces").Sum(s => s.Total))}");

            List<string> sellers = sales.GroupBy(s => s.CashierName)
                .Select(g => (name: g.Key, total: g.Sum(s => s.Total), count: g.Count()))
                .OrderByDescending(g => g.total).Take(5)
                .Select(g => $"{g.name} : {Money(g.total)} ({g.count})").ToList();
            panel.TextLines.Add("");
            panel.TextLines.Add(Bold("Meilleurs vendeurs"));
            panel.TextLines.Add(sellers.Count > 0 ? string.Join("<br>", sellers) : "-");

            Dictionary<string, int> top = new Dictionary<string, int>();
            foreach (BakerySale sale in sales)
            {
                foreach (string line in (sale.Details ?? "").Split('\n'))
                {
                    Match m = DetailLine.Match(line);
                    if (!m.Success) continue;
                    string name = m.Groups[2].Value;
                    top[name] = (top.TryGetValue(name, out int q) ? q : 0) + int.Parse(m.Groups[1].Value);
                }
            }
            panel.TextLines.Add("");
            panel.TextLines.Add(Bold("Articles les plus vendus"));
            panel.TextLines.Add(top.Count > 0
                ? string.Join("<br>", top.OrderByDescending(t => t.Value).Take(5).Select(t => $"{t.Key} : {t.Value}"))
                : "-");

            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void History(Player player, int bizId)
        {
            List<BakerySale> sales = (await BakerySale.Query(s => s.BizId == bizId))
                .OrderByDescending(s => s.Date).Take(50).ToList();

            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand("Boulangerie - Historique"), UIPanel.PanelType.TabPrice, player, () => History(player, bizId));
            if (sales.Count == 0)
                panel.AddTabLine("Aucune vente pour le moment", "", DefaultIcon, _ => panel.Refresh());
            foreach (BakerySale sale in sales)
            {
                BakerySale s = sale;
                panel.AddTabLine($"#{s.Id} {FormatDate(s.Date)} - {s.ClientName}<br>" + Size(Color($"par {s.CashierName} - {s.PaymentMethod}", Colors.Grey), 14),
                    Money(s.Total), DefaultIcon, _ => SaleDetail(player, s));
            }
            if (sales.Count > 0)
                panel.NextButton("Voir le ticket", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void SaleDetail(Player player, BakerySale sale)
        {
            Panel panel = PanelHelper.Create(BoulangeriePlugin.Brand("Boulangerie - Ticket"), UIPanel.PanelType.Text, player, () => SaleDetail(player, sale));
            panel.TextLines.Add(Bold($"Ticket #{sale.Id} - {FormatDate(sale.Date)}"));
            panel.TextLines.Add($"Caisse : {sale.RegisterName}");
            panel.TextLines.Add($"Vendeur : {sale.CashierName}");
            panel.TextLines.Add($"Client : {sale.ClientName}");
            panel.TextLines.Add("");
            panel.TextLines.Add((sale.Details ?? "").Replace("\n", "<br>"));
            panel.TextLines.Add("");
            if (sale.DiscountPercent > 0)
                panel.TextLines.Add($"Remise : {sale.DiscountPercent}%");
            panel.TextLines.Add(Bold($"Total : {Money(sale.Total)} ({sale.PaymentMethod})"));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static string FormatDate(long unix) =>
            DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("dd/MM HH:mm");
    }
}
