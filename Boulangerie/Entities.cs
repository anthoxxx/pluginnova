using System.Collections.Generic;
using ModKit.ORM;
using Newtonsoft.Json;
using SQLite;

namespace Boulangerie
{
    /// <summary>Configuration globale du plugin (une seule ligne en base), modifiable dans AAMenu.</summary>
    public class BakeryConfig : ModEntity<BakeryConfig>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        /// <summary>Webhook Discord par défaut (une caisse peut avoir le sien).</summary>
        public string WebhookUrl { get; set; } = "";

        public bool WebhookEnabled { get; set; } = false;

        /// <summary>Couleur des embeds Discord (#RRGGBB).</summary>
        public string WebhookColor { get; set; } = "#E8A33D";

        /// <summary>Distance max (m) entre le boulanger et le client pour l'encaisser.</summary>
        public float MaxClientDistance { get; set; } = 6f;

        /// <summary>Part du ticket versée au boulanger qui encaisse (le reste va dans le compte de l'entreprise).</summary>
        public int CommissionPercent { get; set; } = 0;

        /// <summary>Remise maximale qu'un employé peut appliquer sur un panier.</summary>
        public int MaxDiscountPercent { get; set; } = 30;

        /// <summary>Le boulanger doit être en service (prise de service métier) pour utiliser la caisse.</summary>
        public bool RequireService { get; set; } = true;

        /// <summary>Le client reçoit les items liés aux produits après paiement.</summary>
        public bool GiveItems { get; set; } = true;

        /// <summary>Les items donnés sont retirés de l'inventaire du boulanger (il doit les avoir).</summary>
        public bool UseCashierStock { get; set; } = false;

        /// <summary>Durée (secondes) pendant laquelle le client peut payer avant que la demande expire.</summary>
        public int PaymentTimeout { get; set; } = 90;

        /// <summary>Durée (secondes) pendant laquelle une commande attend au point Carte.</summary>
        public int PointOrderTimeout { get; set; } = 600;

        /// <summary>Les employés (pas seulement le patron) peuvent modifier la carte.</summary>
        public bool EmployeesCanEditMenu { get; set; } = false;

        /// <summary>Icône du jeu des lignes sans icône propre (null = icône vide).</summary>
        public int? DefaultIconId { get; set; }

        /// <summary>Icône du jeu des formules sans icône propre (null = icône par défaut).</summary>
        public int? FormulaIconId { get; set; }
    }

    /// <summary>Produit vendu par une boulangerie (entreprise).</summary>
    public class BakeryProduct : ModEntity<BakeryProduct>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int BizId { get; set; }

        public string Name { get; set; }

        public double Price { get; set; }

        /// <summary>Item du jeu remis au client (0 = aucun item, simple prestation).</summary>
        public int ItemId { get; set; }

        /// <summary>Faux = en rupture, n'apparaît pas à la caisse.</summary>
        public bool Available { get; set; } = true;

        /// <summary>Vrai : la quantité <see cref="Stock"/> est suivie ; faux : stock illimité.</summary>
        public bool TrackStock { get; set; }

        /// <summary>Quantité en stock (utilisée seulement si <see cref="TrackStock"/>).</summary>
        public int Stock { get; set; }

        /// <summary>Icône du jeu affichée sur les panels (null = icône de l'item lié).</summary>
        public int? IconId { get; set; }
    }

    /// <summary>Formule maison (ex. croissant + café) vendue à prix fixe.</summary>
    public class BakeryFormula : ModEntity<BakeryFormula>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int BizId { get; set; }

        public string Name { get; set; }

        public double Price { get; set; }

        /// <summary>Contenu JSON : id du produit → quantité.</summary>
        public string Content { get; set; } = "{}";

        public bool Available { get; set; } = true;

        /// <summary>Icône du jeu affichée sur les panels (null = icône des formules de la configuration).</summary>
        public int? IconId { get; set; }

        [Ignore]
        public Dictionary<int, int> Items
        {
            get => JsonConvert.DeserializeObject<Dictionary<int, int>>(Content ?? "{}") ?? new Dictionary<int, int>();
            set => Content = JsonConvert.SerializeObject(value ?? new Dictionary<int, int>());
        }
    }

    /// <summary>Ticket de caisse enregistré après chaque vente.</summary>
    public class BakerySale : ModEntity<BakerySale>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int BizId { get; set; }

        public string RegisterName { get; set; }

        public int CashierId { get; set; }

        public string CashierName { get; set; }

        public int ClientId { get; set; }

        public string ClientName { get; set; }

        public double Total { get; set; }

        public int DiscountPercent { get; set; }

        /// <summary>"Espèces" ou "Carte bancaire".</summary>
        public string PaymentMethod { get; set; }

        /// <summary>Détail lisible du ticket (une ligne par article).</summary>
        public string Details { get; set; }

        /// <summary>Date Unix (secondes).</summary>
        public long Date { get; set; }
    }
}
