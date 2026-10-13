using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static ModKit.Helper.TextFormattingHelper;

namespace Boulangerie
{
    /// <summary>Stock des produits : calcul des besoins d'un panier, vérification et déstockage.</summary>
    public static class StockHelper
    {
        /// <summary>Quantité de chaque produit (id → quantité) contenue dans le panier, formules comprises.</summary>
        public static Dictionary<int, int> Needs(Cart cart, Dictionary<int, BakeryFormula> formulas)
        {
            Dictionary<int, int> needs = new Dictionary<int, int>();
            void Add(int productId, int quantity) =>
                needs[productId] = (needs.TryGetValue(productId, out int q) ? q : 0) + quantity;

            foreach (CartLine line in cart.Lines)
            {
                if (!line.IsFormula)
                    Add(line.RefId, line.Quantity);
                else if (formulas.TryGetValue(line.RefId, out BakeryFormula formula))
                    foreach (KeyValuePair<int, int> content in formula.Items)
                        Add(content.Key, content.Value * line.Quantity);
            }
            return needs;
        }

        /// <summary>Produits dont le stock ne suffit pas pour ce panier (ex. « Croissant (reste 2) »).</summary>
        public static List<string> Missing(Cart cart, Dictionary<int, BakeryProduct> products, Dictionary<int, BakeryFormula> formulas)
        {
            return Needs(cart, formulas)
                .Where(n => products.TryGetValue(n.Key, out BakeryProduct p) && p.TrackStock && p.Stock < n.Value)
                .Select(n => $"{products[n.Key].Name} (reste {products[n.Key].Stock})")
                .ToList();
        }

        /// <summary>Le panier passe-t-il si on y ajoute cet article ?</summary>
        public static List<string> MissingIfAdded(Cart cart, bool isFormula, int refId, string name, double price, int quantity,
            Dictionary<int, BakeryProduct> products, Dictionary<int, BakeryFormula> formulas)
        {
            Cart test = cart.Snapshot();
            test.Add(isFormula, refId, name, price, quantity);
            return Missing(test, products, formulas);
        }

        public static async Task<(Dictionary<int, BakeryProduct> products, Dictionary<int, BakeryFormula> formulas)> Load(int bizId)
        {
            Dictionary<int, BakeryProduct> products = (await BakeryProduct.Query(p => p.BizId == bizId)).ToDictionary(p => p.Id);
            Dictionary<int, BakeryFormula> formulas = (await BakeryFormula.Query(f => f.BizId == bizId)).ToDictionary(f => f.Id);
            return (products, formulas);
        }

        /// <summary>Retire du stock les produits vendus. Renvoie les produits désormais épuisés.</summary>
        public static async Task<List<string>> Consume(Cart cart, Dictionary<int, BakeryProduct> products, Dictionary<int, BakeryFormula> formulas)
        {
            List<string> soldOut = new List<string>();
            foreach (KeyValuePair<int, int> need in Needs(cart, formulas))
            {
                if (!products.TryGetValue(need.Key, out BakeryProduct product) || !product.TrackStock) continue;
                product.Stock = System.Math.Max(0, product.Stock - need.Value);
                await product.Save();
                if (product.Stock == 0) soldOut.Add(product.Name);
            }
            return soldOut;
        }

        public static bool InStock(BakeryProduct product, int quantity = 1) =>
            product.Available && (!product.TrackStock || product.Stock >= quantity);

        /// <summary>Une formule est disponible si elle est en vente et que tout son contenu est en stock.</summary>
        public static bool FormulaAvailable(BakeryFormula formula, Dictionary<int, BakeryProduct> products)
        {
            Dictionary<int, int> items = formula.Items;
            return formula.Available && items.Count > 0 &&
                   items.All(i => products.TryGetValue(i.Key, out BakeryProduct p) && InStock(p, i.Value));
        }

        /// <summary>Texte d'état affiché sur la carte et à la caisse.</summary>
        public static string Label(BakeryProduct product)
        {
            if (!product.Available) return Color("Indisponible", Colors.Error);
            if (!product.TrackStock) return Color("Disponible", Colors.Success);
            if (product.Stock <= 0) return Color("Épuisé", Colors.Error);
            if (product.Stock <= 5) return Color($"Plus que {product.Stock} !", Colors.Warning);
            return Color($"En stock : {product.Stock}", Colors.Success);
        }
    }
}
