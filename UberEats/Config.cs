using System;
using System.IO;
using ModKit.Internal;
using Newtonsoft.Json;

namespace UberEats
{
    /// <summary>Réglages de la plateforme, modifiables en jeu (AAMenu > Administration > Plugins > Uber Eats).</summary>
    public class UberEatsConfig
    {
        /// <summary>Part prélevée par la plateforme sur le montant des plats (en %).</summary>
        public double CommissionPercent { get; set; } = 10;
        /// <summary>Frais de livraison fixes payés par le client.</summary>
        public double BaseDeliveryFee { get; set; } = 15;
        /// <summary>Frais ajoutés tous les 100 m entre le restaurant et le client.</summary>
        public double FeePer100m { get; set; } = 2;
        public double MaxDeliveryFee { get; set; } = 80;
        public double MinOrderAmount { get; set; } = 10;
        /// <summary>Prix d'ouverture d'un restaurant sur la plateforme.</summary>
        public double RestaurantCreationCost { get; set; } = 500;
        public int MaxActiveOrdersPerClient { get; set; } = 2;
        /// <summary>Distance max (m) entre le livreur et le client pour remettre le sac.</summary>
        public float HandoverDistance { get; set; } = 6f;
        /// <summary>Distance max (m) entre le livreur et le restaurant pour récupérer le sac.</summary>
        public float PickupDistance { get; set; } = 10f;
        /// <summary>Une commande non acceptée par le restaurant est annulée et remboursée après ce délai (minutes).</summary>
        public int PendingTimeoutMinutes { get; set; } = 15;
        /// <summary>Une commande prête que personne ne récupère est annulée et remboursée après ce délai (minutes).</summary>
        public int NoCourierTimeoutMinutes { get; set; } = 45;

        [JsonIgnore]
        public static string FilePath { get; set; }

        public static UberEatsConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    UberEatsConfig config = JsonConvert.DeserializeObject<UberEatsConfig>(File.ReadAllText(FilePath));
                    if (config != null)
                    {
                        config.Save(); // ajoute les nouvelles clés après une mise à jour
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("UberEats", "Config illisible, valeurs par défaut utilisées : " + ex.Message);
            }

            UberEatsConfig created = new UberEatsConfig();
            created.Save();
            return created;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Logger.LogError("UberEats", "Impossible d'enregistrer la config : " + ex.Message);
            }
        }
    }
}
