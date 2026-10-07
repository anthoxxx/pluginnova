using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Life;
using Life.InventorySystem;
using Life.Network;
using Life.UI;
using UnityEngine;

namespace PNJCreator
{
    /// <summary>Éditeur in-game (/Configpnjcreator). Chaque modification est sauvegardée immédiatement.</summary>
    public class Menus
    {
        readonly PNJCreator plugin;
        DataStore Store => plugin.Store;
        NpcManager Npcs => plugin.Npcs;
        const string Title = PNJCreator.DisplayName;

        public Menus(PNJCreator plugin) { this.plugin = plugin; }

        // ==================================================================
        //  Outils de menu
        // ==================================================================

        static UIPanel NewTab(string title) => new UIPanel(title, UIPanel.PanelType.TabPrice);

        /// <summary>Ajoute une ligne qui ferme le menu puis exécute l'action.</summary>
        static void Line(UIPanel panel, Player player, string label, string value, int icon, Action next)
        {
            panel.AddTabLine(label, value ?? "", icon, ui =>
            {
                player.ClosePanel(ui);
                next();
            });
        }

        void Show(Player player, UIPanel panel, Action back)
        {
            panel.AddButton("Sélectionner", ui => ui.SelectTab());
            if (back != null) panel.AddButton("Retour", ui => { player.ClosePanel(ui); back(); });
            panel.AddButton("Fermer", ui => player.ClosePanel(ui));
            player.ShowPanelUI(panel);
        }

        void AskText(Player player, string title, string text, string placeholder, Action<string> onValidate, Action back, bool password = false)
        {
            var panel = new UIPanel(title, UIPanel.PanelType.Input).SetText(text).SetInputPlaceholder(placeholder);
            panel.isPassword = password;
            panel.AddButton("Valider", ui =>
            {
                string value = ui.inputText ?? "";
                player.ClosePanel(ui);
                onValidate(value.Trim());
            });
            panel.AddButton("Retour", ui => { player.ClosePanel(ui); back(); });
            player.ShowPanelUI(panel);
        }

        void AskNumber(Player player, string title, string label, float current, float min, float max, Action<float> onValidate, Action back)
        {
            AskText(player, title,
                $"{label}\nValeur actuelle : {Fmt(current)}\nValeurs acceptées : {Fmt(min)} à {Fmt(max)}",
                Fmt(current),
                value =>
                {
                    if (!TryParse(value, out float f) || f < min || f > max)
                    {
                        player.Notify(Title, $"Valeur invalide : entrez un nombre entre {Fmt(min)} et {Fmt(max)}.", NotificationManager.Type.Error);
                        AskNumber(player, title, label, current, min, max, onValidate, back);
                        return;
                    }
                    onValidate(f);
                }, back);
        }

        void Confirm(Player player, string title, string text, Action yes, Action no)
        {
            var panel = new UIPanel(title, UIPanel.PanelType.Text).SetText(text);
            panel.AddButton("Confirmer", ui => { player.ClosePanel(ui); yes(); });
            panel.AddButton("Annuler", ui => { player.ClosePanel(ui); no(); });
            player.ShowPanelUI(panel);
        }

        void Info(Player player, string title, string text, Action back)
        {
            var panel = new UIPanel(title, UIPanel.PanelType.Text).SetText(text);
            panel.AddButton("Retour", ui => { player.ClosePanel(ui); back(); });
            player.ShowPanelUI(panel);
        }

        static bool TryParse(string s, out float value) =>
            float.TryParse((s ?? "").Replace(',', '.').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !float.IsNaN(value) && !float.IsInfinity(value);

        static string Fmt(float f) => f.ToString("0.##", CultureInfo.GetCultureInfo("fr-FR"));
        static string Tokens(long n) => n.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');
        static string OnOff(bool b) => b ? "Activé" : "Désactivé";
        static string Short(string s, int max) => string.IsNullOrEmpty(s) ? "—" : s.Length <= max ? s : s.Substring(0, max) + "…";

        bool Guard(Player player)
        {
            // Les droits peuvent changer pendant que le menu est ouvert.
            return plugin.RequireEditor(player);
        }

        // ------------------------------------------------------------------
        //  Icônes : MenuIcons du fichier de config (id ou slug d'item), sinon icône du jeu adaptée.
        // ------------------------------------------------------------------

        int Icon(string key, Func<Sprite> auto = null)
        {
            try
            {
                if (Store.Config.MenuIcons.TryGetValue(key, out string itemRef) && !string.IsNullOrWhiteSpace(itemRef))
                {
                    Item item = int.TryParse(itemRef, out int id) ? Nova.man.item.GetItem(id) : Nova.man.item.GetItem(itemRef.Trim());
                    if (item != null) return IconIndex(item.Icon);
                }
                return auto != null ? IconIndex(auto()) : -1;
            }
            catch { return -1; }
        }

        static int IconIndex(Sprite sprite) => sprite == null ? -1 : Nova.man.newIcons.IndexOf(sprite);

        static Sprite ClothIcon(ClothType type, int sex) =>
            Nova.server.buyableCloths.Where(c => c.clothType == (int)type && c.sexId == sex).Select(c => c.icon).FirstOrDefault(i => i != null);

        static Sprite HairIcon(int sex) => Nova.server.hairCuts.Where(h => h.sexId == sex).Select(h => h.icon).FirstOrDefault(i => i != null);
        static Sprite BeardIcon() => Nova.server.beardCuts.Select(b => b.icon).FirstOrDefault(i => i != null);

        // ==================================================================
        //  Menu principal
        // ==================================================================

        public void Main(Player player)
        {
            if (!Guard(player)) return;
            var ai = Store.Config.Ai;
            var panel = NewTab(Title);
            Line(panel, player, "Créer un PNJ à ma position", "", Icon("create", () => ClothIcon(ClothType.Shirt, 0)), () => CreateName(player));
            Line(panel, player, "Liste des PNJ", Store.Config.Npcs.Count.ToString(), Icon("list", () => HairIcon(0)), () => List(player));
            Line(panel, player, "Fournisseurs d'IA", ai.ActiveProvider, Icon("ai"), () => AiProvidersMenu(player));
            Line(panel, player, "Consommation de l'IA", Tokens(Store.Usage.Total.InputTokens + Store.Usage.Total.OutputTokens) + " tokens", Icon("usage"), () => UsageMenu(player));
            Line(panel, player, "Réglages", "", Icon("settings"), () => SettingsMenu(player));
            Line(panel, player, "Recharger la configuration", "", Icon("reload"), () =>
            {
                plugin.Reload();
                player.Notify(Title, $"Configuration rechargée : {Store.Config.Npcs.Count} PNJ.", NotificationManager.Type.Success);
                Main(player);
            });
            Show(player, panel, null);
        }

        // ==================================================================
        //  Création
        // ==================================================================

        void CreateName(Player player)
        {
            AskText(player, "Nouveau PNJ", "Nom du PNJ (affiché au-dessus de sa tête) :", "Ex. : Gérard le boulanger", name =>
            {
                name = Nova.RemoveTextFormat(name).Trim();
                if (name.Length == 0 || name.Length > 40)
                {
                    player.Notify(Title, "Le nom doit faire entre 1 et 40 caractères.", NotificationManager.Type.Error);
                    CreateName(player);
                    return;
                }
                CreateSex(player, name);
            }, () => Main(player));
        }

        void CreateSex(Player player, string name)
        {
            var panel = NewTab("Sexe de « " + name + " »");
            Line(panel, player, "Homme", "", Icon("male", () => HairIcon(0)), () => Create(player, name, 0));
            Line(panel, player, "Femme", "", Icon("female", () => HairIcon(1)), () => Create(player, name, 1));
            Show(player, panel, () => CreateName(player));
        }

        void Create(Player player, string name, int sex)
        {
            if (!Guard(player)) return;
            if (!NpcManager.ServerReady)
            {
                player.Notify(Title, "Le serveur n'est pas encore prêt, réessayez dans quelques secondes.", NotificationManager.Type.Warning);
                return;
            }
            var ai = Store.Config.Ai;
            var data = new NpcData
            {
                Id = Store.NextNpcId(),
                Name = name,
                Sex = sex,
                Position = PNJCreator.PlayerPosition(player),
                RotationY = PNJCreator.PlayerRotationY(player),
                TalkRange = ai.DefaultTalkRange,
                HearRange = ai.DefaultHearRange,
            };

            // Base d'apparence : celle du créateur (couleurs de peau, visage…), vêtements adaptés au sexe choisi.
            CharacterCustomizationSetup skin = NpcManager.CloneSkin(player.setup.characterSkinData) ?? new CharacterCustomizationSetup();
            if ((player.character.SexId == 1 ? 1 : 0) != sex) ResetForSex(skin, sex);
            NpcManager.StoreSkin(data, skin);

            Store.Config.Npcs.Add(data);
            Store.SaveConfig();
            Npcs.Spawn(data);
            player.Notify(Title, $"PNJ « {name} » créé (#{data.Id}).", NotificationManager.Type.Success);
            NpcMenu(player, data.Id);
        }

        /// <summary>Les coupes et vêtements dépendent du sexe : on repart sur des valeurs valides.</summary>
        static void ResetForSex(CharacterCustomizationSetup skin, int sex)
        {
            skin.Hair = Nova.server.hairCuts.Where(h => h.sexId == sex).Select(h => h.hairId).DefaultIfEmpty(0).First();
            skin.Beard = -1;
            skin.Hat = -1;
            skin.Accessory = -1;
            skin.TShirt = FirstCloth(ClothType.Shirt, sex);
            skin.Pants = FirstCloth(ClothType.Pants, sex);
            skin.Shoes = FirstCloth(ClothType.Shoes, sex);
            skin.tshirtData = EmptyClothData();
            skin.pantsData = EmptyClothData();
            if (sex == 0) skin.BreastSize = 0f;
        }

        static int FirstCloth(ClothType type, int sex) =>
            Nova.server.buyableCloths.Where(c => c.clothType == (int)type && c.sexId == sex).Select(c => c.clothId).DefaultIfEmpty(-1).First();

        static ClothData EmptyClothData() => new ClothData { clothName = "", url = "" };

        // ==================================================================
        //  Liste et fiche d'un PNJ
        // ==================================================================

        void List(Player player)
        {
            if (!Guard(player)) return;
            var panel = NewTab($"PNJ ({Store.Config.Npcs.Count})");
            Vector3 pos = PNJCreator.PlayerPosition(player);
            foreach (var npc in Store.Config.Npcs.OrderBy(n => Vector3.Distance(pos, n.Position)))
            {
                int id = npc.Id;
                string info = $"#{id} · {(npc.Mobile ? "mobile" : "statique")}{(npc.AiEnabled ? " · IA" : "")} · {Vector3.Distance(pos, npc.Position):0} m";
                Line(panel, player, npc.Name, info, Icon(npc.Sex == 1 ? "female" : "male", () => HairIcon(npc.Sex)), () => NpcMenu(player, id));
            }
            if (Store.Config.Npcs.Count == 0)
                Line(panel, player, "Aucun PNJ : en créer un", "", Icon("create"), () => CreateName(player));
            Show(player, panel, () => Main(player));
        }

        NpcData Find(Player player, int id)
        {
            var npc = Store.Config.Npcs.FirstOrDefault(n => n.Id == id);
            if (npc == null) player.Notify(Title, "Ce PNJ n'existe plus.", NotificationManager.Type.Error);
            return npc;
        }

        void NpcMenu(Player player, int id)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) { List(player); return; }

            var panel = NewTab($"PNJ #{id} — {npc.Name}");
            Line(panel, player, "Renommer", npc.Name, Icon("rename"), () => Rename(player, id));
            Line(panel, player, "Sexe", npc.SexLabel, Icon(npc.Sex == 1 ? "female" : "male", () => HairIcon(npc.Sex)), () => ToggleSex(player, id));
            Line(panel, player, "Échelle", "x" + Fmt(npc.Scale), Icon("scale"), () =>
                AskNumber(player, "Échelle", "Taille globale du PNJ (1 = normale).", npc.Scale, 0.5f, 2f, v =>
                {
                    npc.Scale = v;
                    Store.SaveConfig();
                    Npcs.Respawn(npc);
                    NpcMenu(player, id);
                }, () => NpcMenu(player, id)));
            Line(panel, player, "Déplacer à ma position", "", Icon("teleport"), () =>
            {
                Npcs.MoveTo(npc, PNJCreator.PlayerPosition(player), PNJCreator.PlayerRotationY(player));
                player.Notify(Title, $"{npc.Name} déplacé à votre position.", NotificationManager.Type.Success);
                NpcMenu(player, id);
            });
            Line(panel, player, "Tourner vers moi", "", Icon("rotate"), () =>
            {
                Vector3 from = Npcs.Instances.TryGetValue(id, out var inst) ? inst.Position : npc.Position;
                Vector3 dir = PNJCreator.PlayerPosition(player) - from;
                dir.y = 0f;
                float angle = dir.sqrMagnitude > 0.001f ? Quaternion.LookRotation(dir).eulerAngles.y : npc.RotationY;
                Npcs.MoveTo(npc, npc.Position, angle);
                NpcMenu(player, id);
            });
            Line(panel, player, "Rotation", Fmt(npc.RotationY) + "°", Icon("rotate"), () =>
                AskNumber(player, "Rotation", "Orientation du PNJ en degrés (0 à 360).", npc.RotationY, 0f, 360f, v =>
                {
                    Npcs.MoveTo(npc, npc.Position, v);
                    NpcMenu(player, id);
                }, () => NpcMenu(player, id)));
            Line(panel, player, "Apparence", "", Icon("appearance", () => ClothIcon(ClothType.Shirt, npc.Sex)), () => AppearanceMenu(player, id));
            Line(panel, player, "Déplacement / patrouille", npc.Mobile ? $"Mobile ({npc.Waypoints.Count} pts)" : "Statique", Icon("move", () => ClothIcon(ClothType.Shoes, npc.Sex)), () => MovementMenu(player, id));
            Line(panel, player, "Intelligence artificielle", OnOff(npc.AiEnabled), Icon("ai"), () => NpcAiMenu(player, id));
            Line(panel, player, "Faire réapparaître", "", Icon("respawn"), () =>
            {
                Npcs.Respawn(npc);
                player.Notify(Title, $"{npc.Name} est réapparu.", NotificationManager.Type.Success);
                NpcMenu(player, id);
            });
            Line(panel, player, "Supprimer", "", Icon("delete"), () =>
                Confirm(player, "Supprimer", $"Supprimer définitivement « {npc.Name} » (#{id}) ?\nSa mémoire de conversation sera effacée.", () =>
                {
                    Npcs.Despawn(id);
                    Store.Config.Npcs.Remove(npc);
                    Store.ClearMemory(id);
                    Store.SaveConfig();
                    player.Notify(Title, $"« {npc.Name} » supprimé.", NotificationManager.Type.Success);
                    List(player);
                }, () => NpcMenu(player, id)));
            Show(player, panel, () => List(player));
        }

        void Rename(Player player, int id)
        {
            var npc = Find(player, id);
            if (npc == null) return;
            AskText(player, "Renommer", $"Nom actuel : {npc.Name}\nNouveau nom :", npc.Name, name =>
            {
                name = Nova.RemoveTextFormat(name).Trim();
                if (name.Length == 0 || name.Length > 40)
                {
                    player.Notify(Title, "Le nom doit faire entre 1 et 40 caractères.", NotificationManager.Type.Error);
                    Rename(player, id);
                    return;
                }
                npc.Name = name;
                Store.SaveConfig();
                Npcs.ApplyName(npc);
                NpcMenu(player, id);
            }, () => NpcMenu(player, id));
        }

        void ToggleSex(Player player, int id)
        {
            var npc = Find(player, id);
            if (npc == null) return;
            npc.Sex = npc.Sex == 1 ? 0 : 1;
            var skin = NpcManager.SkinOf(npc) ?? new CharacterCustomizationSetup();
            ResetForSex(skin, npc.Sex);
            NpcManager.StoreSkin(npc, skin);
            Store.SaveConfig();
            Npcs.Respawn(npc); // le modèle 3D change : nouveau prefab
            player.Notify(Title, $"{npc.Name} est maintenant : {npc.SexLabel}. Coupe et tenue réinitialisées.", NotificationManager.Type.Info);
            NpcMenu(player, id);
        }

        // ==================================================================
        //  Apparence
        // ==================================================================

        void AppearanceMenu(Player player, int id)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) return;
            var skin = NpcManager.SkinOf(npc) ?? new CharacterCustomizationSetup();

            var panel = NewTab($"Apparence — {npc.Name}");
            Line(panel, player, "Copier mon apparence", "visage, tenue, couleurs", Icon("copy", () => HairIcon(npc.Sex)), () => CopyMyAppearance(player, id));
            Line(panel, player, "Modifier la tenue", "", Icon("outfit", () => ClothIcon(ClothType.Shirt, npc.Sex)), () => OutfitMenu(player, id));
            Line(panel, player, "Vêtements custom (flocage)", HasUrl(skin.tshirtData) || HasUrl(skin.pantsData) ? "Oui" : "Non", Icon("custom", () => ClothIcon(ClothType.Shirt, npc.Sex)), () => CustomClothMenu(player, id));
            Line(panel, player, "Coupe de cheveux", "n°" + skin.Hair, Icon("hair", () => HairIcon(npc.Sex)), () => HairMenu(player, id));
            if (npc.Sex == 0)
                Line(panel, player, "Barbe", skin.Beard < 0 ? "Aucune" : "n°" + skin.Beard, Icon("beard", BeardIcon), () => BeardMenu(player, id));
            Line(panel, player, "Couleur des cheveux", "", Icon("haircolor", () => HairIcon(npc.Sex)), () => HairColorMenu(player, id));
            Line(panel, player, "Morphologie", "", Icon("morpho"), () => MorphoMenu(player, id));
            Show(player, panel, () => NpcMenu(player, id));
        }

        static bool HasUrl(ClothData d) => !string.IsNullOrEmpty(d.url) && d.url.Length > 1;

        void CopyMyAppearance(Player player, int id)
        {
            var npc = Find(player, id);
            if (npc == null) return;
            var skin = NpcManager.CloneSkin(player.setup.characterSkinData);
            if (skin == null)
            {
                player.Notify(Title, "Impossible de lire votre apparence.", NotificationManager.Type.Error);
                AppearanceMenu(player, id);
                return;
            }
            int sex = player.character.SexId == 1 ? 1 : 0;
            bool sexChanged = sex != npc.Sex;
            npc.Sex = sex;
            NpcManager.StoreSkin(npc, skin);
            Store.SaveConfig();
            if (sexChanged) Npcs.Respawn(npc); else Npcs.ApplySkin(npc);
            player.Notify(Title, $"Votre apparence a été appliquée à {npc.Name}.", NotificationManager.Type.Success);
            AppearanceMenu(player, id);
        }

        // ---------------- Tenue ----------------

        static readonly (ClothType type, string label)[] OutfitSlots =
        {
            (ClothType.Shirt, "Haut"),
            (ClothType.Pants, "Bas"),
            (ClothType.Shoes, "Chaussures"),
            (ClothType.Hat, "Chapeau"),
            (ClothType.Accessory, "Accessoire"),
        };

        static int GetCloth(CharacterCustomizationSetup s, ClothType t)
        {
            switch (t)
            {
                case ClothType.Hat: return s.Hat;
                case ClothType.Accessory: return s.Accessory;
                case ClothType.Shirt: return s.TShirt;
                case ClothType.Pants: return s.Pants;
                default: return s.Shoes;
            }
        }

        static void SetCloth(CharacterCustomizationSetup s, ClothType t, int clothId)
        {
            switch (t)
            {
                case ClothType.Hat: s.Hat = clothId; break;
                case ClothType.Accessory: s.Accessory = clothId; break;
                // Un vêtement standard remplace un éventuel flocage.
                case ClothType.Shirt: s.TShirt = clothId; s.tshirtData = EmptyClothData(); break;
                case ClothType.Pants: s.Pants = clothId; s.pantsData = EmptyClothData(); break;
                default: s.Shoes = clothId; break;
            }
        }

        string ClothName(ClothType type, int sex, int clothId)
        {
            if (clothId < 0) return "Aucun";
            var c = Nova.server.buyableCloths.FirstOrDefault(b => b.clothType == (int)type && b.sexId == sex && b.clothId == clothId);
            return string.IsNullOrEmpty(c.name) ? "n°" + clothId : c.name;
        }

        void OutfitMenu(Player player, int id)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) return;
            var skin = NpcManager.SkinOf(npc) ?? new CharacterCustomizationSetup();
            var panel = NewTab($"Tenue — {npc.Name}");
            foreach (var (type, label) in OutfitSlots)
            {
                var t = type;
                Line(panel, player, label, ClothName(t, npc.Sex, GetCloth(skin, t)), Icon("cloth_" + t, () => ClothIcon(t, npc.Sex)), () => ClothList(player, id, t));
            }
            Show(player, panel, () => AppearanceMenu(player, id));
        }

        void ClothList(Player player, int id, ClothType type)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) return;
            var skin = NpcManager.SkinOf(npc) ?? new CharacterCustomizationSetup();
            int current = GetCloth(skin, type);
            string label = OutfitSlots.First(s => s.type == type).label;

            var panel = NewTab($"{label} — {npc.Name}");
            Line(panel, player, "Aucun (retirer)", current < 0 ? "✔" : "", -1, () =>
            {
                Npcs.EditSkin(npc, s => SetCloth(s, type, -1));
                ClothList(player, id, type);
            });
            foreach (var cloth in Nova.server.buyableCloths.Where(c => c.clothType == (int)type && c.sexId == npc.Sex))
            {
                int clothId = cloth.clothId;
                string name = string.IsNullOrEmpty(cloth.name) ? "Vêtement n°" + clothId : cloth.name;
                // Aperçu direct : le vêtement est appliqué au clic et le menu reste ouvert.
                Line(panel, player, name, clothId == current ? "✔" : "", IconIndex(cloth.icon), () =>
                {
                    Npcs.EditSkin(npc, s => SetCloth(s, type, clothId));
                    ClothList(player, id, type);
                });
            }
            Show(player, panel, () => OutfitMenu(player, id));
        }

        // ---------------- Flocage ----------------

        static int FirstCustomCloth(ClothType type, int sex)
        {
            var item = Nova.man.item.items.OfType<Cloth>().FirstOrDefault(c => c.isCustom && c.type == type && c.isMale == (sex == 0));
            return item != null ? item.clothId : -1;
        }

        void CustomClothMenu(Player player, int id)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) return;
            var skin = NpcManager.SkinOf(npc) ?? new CharacterCustomizationSetup();

            var panel = NewTab($"Flocage — {npc.Name}");
            Line(panel, player, "Image du haut (lien)", HasUrl(skin.tshirtData) ? Short(skin.tshirtData.url, 30) : "Aucune", Icon("custom", () => ClothIcon(ClothType.Shirt, npc.Sex)),
                () => AskCustomUrl(player, id, ClothType.Shirt));
            Line(panel, player, "Image du bas (lien)", HasUrl(skin.pantsData) ? Short(skin.pantsData.url, 30) : "Aucune", Icon("custom", () => ClothIcon(ClothType.Pants, npc.Sex)),
                () => AskCustomUrl(player, id, ClothType.Pants));
            if (HasUrl(skin.tshirtData))
                Line(panel, player, "Retirer le flocage du haut", "", -1, () =>
                {
                    Npcs.EditSkin(npc, s => s.tshirtData = EmptyClothData());
                    CustomClothMenu(player, id);
                });
            if (HasUrl(skin.pantsData))
                Line(panel, player, "Retirer le flocage du bas", "", -1, () =>
                {
                    Npcs.EditSkin(npc, s => s.pantsData = EmptyClothData());
                    CustomClothMenu(player, id);
                });
            Show(player, panel, () => AppearanceMenu(player, id));
        }

        void AskCustomUrl(Player player, int id, ClothType type)
        {
            var npc = Find(player, id);
            if (npc == null) return;
            string part = type == ClothType.Shirt ? "haut" : "bas";
            AskText(player, "Flocage du " + part,
                $"Collez le lien direct de l'image (http/https, .png ou .jpg) que {npc.Name} portera sur son {part}.",
                "https://…/image.png", url =>
                {
                    if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) || url.Length > 500 || url.Contains(" "))
                    {
                        player.Notify(Title, "Lien invalide : il doit commencer par http:// ou https://.", NotificationManager.Type.Error);
                        CustomClothMenu(player, id);
                        return;
                    }
                    Npcs.EditSkin(npc, s =>
                    {
                        // Le flocage s'applique sur un vêtement « custom » du jeu quand il en existe un.
                        int customId = FirstCustomCloth(type, npc.Sex);
                        var data = new ClothData { clothName = npc.Name, url = url };
                        if (type == ClothType.Shirt) { if (customId >= 0) s.TShirt = customId; s.tshirtData = data; }
                        else { if (customId >= 0) s.Pants = customId; s.pantsData = data; }
                    });
                    player.Notify(Title, $"Flocage du {part} appliqué à {npc.Name}.", NotificationManager.Type.Success);
                    CustomClothMenu(player, id);
                }, () => CustomClothMenu(player, id));
        }

        // ---------------- Cheveux / barbe ----------------

        void HairMenu(Player player, int id)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) return;
            int current = (NpcManager.SkinOf(npc) ?? new CharacterCustomizationSetup()).Hair;
            var panel = NewTab($"Coupe — {npc.Name}");
            int n = 1;
            foreach (var cut in Nova.server.hairCuts.Where(h => h.sexId == npc.Sex))
            {
                int hairId = cut.hairId;
                Line(panel, player, "Coupe n°" + n++, hairId == current ? "✔" : "", IconIndex(cut.icon), () =>
                {
                    Npcs.EditSkin(npc, s => s.Hair = hairId);
                    HairMenu(player, id);
                });
            }
            Show(player, panel, () => AppearanceMenu(player, id));
        }

        void BeardMenu(Player player, int id)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) return;
            int current = (NpcManager.SkinOf(npc) ?? new CharacterCustomizationSetup()).Beard;
            var panel = NewTab($"Barbe — {npc.Name}");
            Line(panel, player, "Aucune (rasé)", current < 0 ? "✔" : "", -1, () =>
            {
                Npcs.EditSkin(npc, s => s.Beard = -1);
                BeardMenu(player, id);
            });
            int n = 1;
            foreach (var cut in Nova.server.beardCuts)
            {
                int beardId = cut.beardId;
                Line(panel, player, "Barbe n°" + n++, beardId == current ? "✔" : "", IconIndex(cut.icon), () =>
                {
                    Npcs.EditSkin(npc, s => s.Beard = beardId);
                    BeardMenu(player, id);
                });
            }
            Show(player, panel, () => AppearanceMenu(player, id));
        }

        void HairColorMenu(Player player, int id)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) return;
            var panel = NewTab($"Couleur des cheveux — {npc.Name}");
            foreach (var hc in Nova.server.hairColors)
            {
                Color color = hc.color;
                string hex = Nova.ColorToHex(color);
                Line(panel, player, $"<color={hex}>■</color> {hc.name}", "", -1, () =>
                {
                    Npcs.EditSkin(npc, s => s.HairColor = color);
                    HairColorMenu(player, id);
                });
            }
            Show(player, panel, () => AppearanceMenu(player, id));
        }

        // ---------------- Morphologie ----------------

        void MorphoMenu(Player player, int id)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) return;
            var s = NpcManager.SkinOf(npc) ?? new CharacterCustomizationSetup();
            var panel = NewTab($"Morphologie — {npc.Name}");

            // Taille : le jeu affiche 1,75 m × (1 + Height).
            Line(panel, player, "Taille", Fmt(175f * (1f + s.Height)) + " cm", Icon("morpho"), () =>
                AskNumber(player, "Taille", "Taille du PNJ en centimètres.", 175f * (1f + s.Height), 150f, 200f,
                    v => { Npcs.EditSkin(npc, k => k.Height = v / 175f - 1f); MorphoMenu(player, id); }, () => MorphoMenu(player, id)));
            MorphoLine(panel, player, npc, "Corpulence", s.Fat, (k, v) => k.Fat = v);
            MorphoLine(panel, player, npc, "Musculature", s.Muscles, (k, v) => k.Muscles = v);
            MorphoLine(panel, player, npc, "Minceur", s.Thin, (k, v) => k.Thin = v);
            MorphoLine(panel, player, npc, "Silhouette fine", s.Slimness, (k, v) => k.Slimness = v);
            if (npc.Sex == 1) MorphoLine(panel, player, npc, "Poitrine", s.BreastSize, (k, v) => k.BreastSize = v);
            Line(panel, player, "Tête", Fmt(s.HeadSize * 100f) + " %", Icon("morpho"), () =>
                AskNumber(player, "Tête", "Taille de la tête en % (0 = normale).", s.HeadSize * 100f, -20f, 20f,
                    v => { Npcs.EditSkin(npc, k => k.HeadSize = v / 100f); MorphoMenu(player, id); }, () => MorphoMenu(player, id)));
            Show(player, panel, () => AppearanceMenu(player, id));
        }

        void MorphoLine(UIPanel panel, Player player, NpcData npc, string label, float value, Action<CharacterCustomizationSetup, float> set)
        {
            int id = npc.Id;
            Line(panel, player, label, Fmt(value), Icon("morpho"), () =>
                AskNumber(player, label, label + " (0 à 100).", value, 0f, 100f,
                    v => { Npcs.EditSkin(npc, k => set(k, v)); MorphoMenu(player, id); }, () => MorphoMenu(player, id)));
        }

        // ==================================================================
        //  Déplacement
        // ==================================================================

        void MovementMenu(Player player, int id)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) return;
            var panel = NewTab($"Déplacement — {npc.Name}");

            Line(panel, player, "Mode", npc.Mobile ? "Mobile (patrouille)" : "Statique", Icon("move"), () =>
            {
                npc.Mobile = !npc.Mobile;
                Store.SaveConfig();
                if (!npc.Mobile) Npcs.Respawn(npc); // retour à sa place d'origine
                else Npcs.ResetPatrol(npc);
                if (npc.Mobile && npc.Waypoints.Count == 0)
                    player.Notify(Title, "Ajoutez des points de passage pour que le PNJ se déplace.", NotificationManager.Type.Info);
                MovementMenu(player, id);
            });
            Line(panel, player, "Ajouter un point ici", npc.Waypoints.Count + " point(s)", Icon("waypoint"), () =>
            {
                npc.Waypoints.Add(new Waypoint(PNJCreator.PlayerPosition(player)));
                Store.SaveConfig();
                player.Notify(Title, $"Point n°{npc.Waypoints.Count} ajouté.", NotificationManager.Type.Success);
                MovementMenu(player, id);
            });
            Line(panel, player, "Points de passage", npc.Waypoints.Count.ToString(), Icon("waypoint"), () => WaypointList(player, id));
            Line(panel, player, "Vitesse", Fmt(npc.Speed) + " m/s", Icon("speed"), () =>
                AskNumber(player, "Vitesse", "Vitesse de marche (1,4 = marche normale).", npc.Speed, 0.3f, 8f,
                    v => { npc.Speed = v; Store.SaveConfig(); MovementMenu(player, id); }, () => MovementMenu(player, id)));
            Line(panel, player, "Pause à chaque point", Fmt(npc.WaitSeconds) + " s", Icon("wait"), () =>
                AskNumber(player, "Pause", "Temps d'arrêt à chaque point, en secondes.", npc.WaitSeconds, 0f, 600f,
                    v => { npc.WaitSeconds = v; Store.SaveConfig(); MovementMenu(player, id); }, () => MovementMenu(player, id)));
            Line(panel, player, "Parcours", npc.LoopPatrol ? "Boucle" : "Aller-retour", Icon("route"), () =>
            {
                npc.LoopPatrol = !npc.LoopPatrol;
                Store.SaveConfig();
                Npcs.ResetPatrol(npc);
                MovementMenu(player, id);
            });
            Line(panel, player, "Ramener à sa position d'origine", "", Icon("respawn"), () =>
            {
                Npcs.Respawn(npc);
                MovementMenu(player, id);
            });
            Show(player, panel, () => NpcMenu(player, id));
        }

        void WaypointList(Player player, int id)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) return;
            var panel = NewTab($"Points de passage — {npc.Name}");
            Vector3 pos = PNJCreator.PlayerPosition(player);
            for (int i = 0; i < npc.Waypoints.Count; i++)
            {
                int index = i;
                Line(panel, player, $"Point n°{i + 1} (supprimer)", $"{Vector3.Distance(pos, npc.Waypoints[i].Position):0} m", Icon("waypoint"), () =>
                {
                    if (index < npc.Waypoints.Count) npc.Waypoints.RemoveAt(index);
                    Store.SaveConfig();
                    Npcs.ResetPatrol(npc);
                    WaypointList(player, id);
                });
            }
            if (npc.Waypoints.Count > 0)
                Line(panel, player, "Tout supprimer", "", Icon("delete"), () =>
                    Confirm(player, "Points de passage", $"Supprimer les {npc.Waypoints.Count} points de {npc.Name} ?", () =>
                    {
                        npc.Waypoints.Clear();
                        Store.SaveConfig();
                        Npcs.Respawn(npc);
                        MovementMenu(player, id);
                    }, () => WaypointList(player, id)));
            Show(player, panel, () => MovementMenu(player, id));
        }

        // ==================================================================
        //  IA d'un PNJ
        // ==================================================================

        void NpcAiMenu(Player player, int id)
        {
            if (!Guard(player)) return;
            var npc = Find(player, id);
            if (npc == null) return;
            var panel = NewTab($"IA — {npc.Name}");
            Line(panel, player, "IA conversationnelle", OnOff(npc.AiEnabled), Icon("ai"), () =>
            {
                npc.AiEnabled = !npc.AiEnabled;
                Store.SaveConfig();
                NpcAiMenu(player, id);
            });
            Line(panel, player, "Personnalité (prompt)", Short(npc.Prompt, 30), Icon("prompt"), () =>
                AskText(player, "Personnalité",
                    $"Rôle et caractère de {npc.Name}. Exemple : « Tu es le boulanger du centre-ville, bourru mais généreux, tu connais tous les ragots du quartier. »\n\nActuel : {Short(npc.Prompt, 300)}",
                    "Tu es…", value =>
                    {
                        if (value.Length == 0) { NpcAiMenu(player, id); return; }
                        npc.Prompt = value.Length > 2000 ? value.Substring(0, 2000) : value;
                        Store.SaveConfig();
                        player.Notify(Title, "Personnalité enregistrée.", NotificationManager.Type.Success);
                        NpcAiMenu(player, id);
                    }, () => NpcAiMenu(player, id)));
            Line(panel, player, "Portée de discussion", Fmt(npc.TalkRange) + " m", Icon("range"), () =>
                AskNumber(player, "Portée de discussion", "Distance maximale pour parler au PNJ avec /say.", npc.TalkRange, 1f, 50f,
                    v => { npc.TalkRange = v; Store.SaveConfig(); NpcAiMenu(player, id); }, () => NpcAiMenu(player, id)));
            Line(panel, player, "Portée d'écoute", Fmt(npc.HearRange) + " m", Icon("range"), () =>
                AskNumber(player, "Portée d'écoute", "Distance à laquelle les joueurs voient l'échange dans le chat.", npc.HearRange, 1f, 100f,
                    v => { npc.HearRange = v; Store.SaveConfig(); NpcAiMenu(player, id); }, () => NpcAiMenu(player, id)));
            int conversations = Store.Memory.Conversations.TryGetValue(id, out var byPlayer) ? byPlayer.Count : 0;
            Line(panel, player, "Effacer la mémoire", conversations + " joueur(s)", Icon("memory"), () =>
                Confirm(player, "Mémoire", $"Effacer toutes les conversations de {npc.Name} ?", () =>
                {
                    Store.ClearMemory(id);
                    Store.SaveMemory();
                    NpcAiMenu(player, id);
                }, () => NpcAiMenu(player, id)));
            Show(player, panel, () => NpcMenu(player, id));
        }

        // ==================================================================
        //  Fournisseurs d'IA
        // ==================================================================

        static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "Aucune";
            if (key.Length <= 8) return "••••";
            return key.Substring(0, 3) + "••••" + key.Substring(key.Length - 4);
        }

        void AiProvidersMenu(Player player)
        {
            if (!Guard(player)) return;
            var ai = Store.Config.Ai;
            var panel = NewTab("Fournisseurs d'IA");
            Line(panel, player, "Fournisseur actif", ai.ActiveProvider, Icon("provider"), () => ActiveProviderMenu(player));
            foreach (string name in AiProviders.All)
            {
                var p = ai.Providers[name];
                string n = name;
                Line(panel, player, name + (name == ai.ActiveProvider ? " (actif)" : ""), (string.IsNullOrEmpty(p.ApiKey) ? "sans clé" : "clé ✔") + " · " + Short(p.Model, 22), Icon("provider"), () => ProviderMenu(player, n));
            }
            Show(player, panel, () => Main(player));
        }

        void ActiveProviderMenu(Player player)
        {
            var ai = Store.Config.Ai;
            var panel = NewTab("Fournisseur actif");
            foreach (string name in AiProviders.All)
            {
                string n = name;
                Line(panel, player, name, name == ai.ActiveProvider ? "✔" : "", Icon("provider"), () =>
                {
                    ai.ActiveProvider = n;
                    Store.SaveConfig();
                    if (string.IsNullOrEmpty(ai.Providers[n].ApiKey))
                        player.Notify(Title, $"{n} est actif mais n'a pas de clé API.", NotificationManager.Type.Warning);
                    AiProvidersMenu(player);
                });
            }
            Show(player, panel, () => AiProvidersMenu(player));
        }

        void ProviderMenu(Player player, string provider)
        {
            if (!Guard(player)) return;
            var ai = Store.Config.Ai;
            var p = ai.Providers[provider];
            var panel = NewTab(provider);
            Line(panel, player, "Clé API", MaskKey(p.ApiKey), Icon("key"), () =>
                AskText(player, "Clé API " + provider, $"Collez la clé API {provider}. Elle est enregistrée dans npcs.config.json.", "Clé API", key =>
                {
                    if (key.Length > 0)
                    {
                        p.ApiKey = key;
                        Store.SaveConfig();
                        player.Notify(Title, $"Clé {provider} enregistrée ({MaskKey(key)}).", NotificationManager.Type.Success);
                    }
                    ProviderMenu(player, provider);
                }, () => ProviderMenu(player, provider), password: true));
            Line(panel, player, "Choisir le modèle", Short(p.Model, 28), Icon("model"), () => FetchModels(player, provider));
            Line(panel, player, "Modèle personnalisé", "", Icon("model"), () =>
                AskText(player, "Modèle " + provider, $"Identifiant exact du modèle.\nActuel : {p.Model}", AiProviders.DefaultModel(provider), model =>
                {
                    if (model.Length > 0 && !model.Contains(" "))
                    {
                        p.Model = model;
                        Store.SaveConfig();
                    }
                    ProviderMenu(player, provider);
                }, () => ProviderMenu(player, provider)));
            Line(panel, player, "Adresse de l'API", Short(p.BaseUrl, 28), Icon("settings"), () =>
                AskText(player, "Adresse de l'API", $"Adresse de base de l'API {provider}.\nPar défaut : {AiProviders.DefaultBaseUrl(provider)}\nActuelle : {p.BaseUrl}", AiProviders.DefaultBaseUrl(provider), url =>
                {
                    if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        p.BaseUrl = url.TrimEnd('/');
                        Store.SaveConfig();
                    }
                    ProviderMenu(player, provider);
                }, () => ProviderMenu(player, provider)));
            if (provider != ai.ActiveProvider)
                Line(panel, player, "Utiliser ce fournisseur", "", Icon("provider"), () =>
                {
                    ai.ActiveProvider = provider;
                    Store.SaveConfig();
                    player.Notify(Title, $"Les PNJ utilisent maintenant {provider}.", NotificationManager.Type.Success);
                    ProviderMenu(player, provider);
                });
            if (!string.IsNullOrEmpty(p.ApiKey))
                Line(panel, player, "Supprimer la clé", "", Icon("delete"), () =>
                    Confirm(player, "Clé API", $"Supprimer la clé {provider} ?", () =>
                    {
                        p.ApiKey = "";
                        Store.SaveConfig();
                        ProviderMenu(player, provider);
                    }, () => ProviderMenu(player, provider)));
            Show(player, panel, () => AiProvidersMenu(player));
        }

        void FetchModels(Player player, string provider)
        {
            var p = Store.Config.Ai.Providers[provider];
            if (string.IsNullOrEmpty(p.ApiKey))
            {
                player.Notify(Title, "Configurez d'abord la clé API.", NotificationManager.Type.Warning);
                ProviderMenu(player, provider);
                return;
            }
            player.Notify(Title, $"Récupération des modèles {provider}…", NotificationManager.Type.Info, 3f);
            var snapshot = new ProviderSettings { ApiKey = p.ApiKey, BaseUrl = p.BaseUrl, Model = p.Model };
            AiClient.ListModelsAsync(provider, snapshot).ContinueWith(task =>
            {
                var (models, error) = task.Exception != null ? (new List<string>(), task.Exception.GetBaseException().Message) : task.Result;
                plugin.Runner.RunOnMainThread(() =>
                {
                    if (!Nova.server.Players.Contains(player)) return;
                    if (error != null || models.Count == 0)
                    {
                        player.Notify(Title, "Liste des modèles indisponible : " + (error ?? "aucun modèle"), NotificationManager.Type.Error, 8f);
                        ProviderMenu(player, provider);
                        return;
                    }
                    ModelList(player, provider, models);
                });
            });
        }

        void ModelList(Player player, string provider, List<string> models)
        {
            var p = Store.Config.Ai.Providers[provider];
            var panel = NewTab($"Modèles {provider} ({models.Count})");
            foreach (string model in models)
            {
                string m = model;
                Line(panel, player, m, m == p.Model ? "✔" : "", Icon("model"), () =>
                {
                    p.Model = m;
                    Store.SaveConfig();
                    player.Notify(Title, $"Modèle {provider} : {m}", NotificationManager.Type.Success);
                    ProviderMenu(player, provider);
                });
            }
            Show(player, panel, () => ProviderMenu(player, provider));
        }

        // ==================================================================
        //  Consommation
        // ==================================================================

        static string UsageLine(UsageCounter c) => $"↓{Tokens(c.InputTokens)} ↑{Tokens(c.OutputTokens)} · {Tokens(c.Requests)} req.";

        void UsageMenu(Player player)
        {
            if (!Guard(player)) return;
            var u = Store.Usage;
            var panel = NewTab("Consommation (depuis le " + DateTimeOffset.FromUnixTimeSeconds(u.SinceUnix).LocalDateTime.ToString("dd/MM/yyyy") + ")");
            Line(panel, player, "Total (entrée / sortie)", UsageLine(u.Total), Icon("usage"), () => UsageMenu(player));
            foreach (string name in AiProviders.All)
            {
                u.ByProvider.TryGetValue(name, out var c);
                Line(panel, player, "Fournisseur " + name, UsageLine(c ?? new UsageCounter()), Icon("provider"), () => UsageMenu(player));
            }
            foreach (var kv in u.ByNpc.OrderByDescending(kv => kv.Value.Total.InputTokens + kv.Value.Total.OutputTokens))
            {
                int npcId = kv.Key;
                string name = Store.Config.Npcs.FirstOrDefault(n => n.Id == npcId)?.Name ?? kv.Value.Name + " (supprimé)";
                Line(panel, player, $"PNJ #{npcId} {name}", UsageLine(kv.Value.Total), Icon("npc"), () => NpcUsageDetail(player, npcId));
            }
            Line(panel, player, "Réinitialiser les compteurs", "", Icon("delete"), () =>
                Confirm(player, "Consommation", "Remettre tous les compteurs de tokens à zéro ?", () =>
                {
                    Store.ResetUsage();
                    player.Notify(Title, "Compteurs réinitialisés.", NotificationManager.Type.Success);
                    UsageMenu(player);
                }, () => UsageMenu(player)));
            Show(player, panel, () => Main(player));
        }

        void NpcUsageDetail(Player player, int npcId)
        {
            if (!Store.Usage.ByNpc.TryGetValue(npcId, out var nu)) { UsageMenu(player); return; }
            var lines = new List<string> { $"PNJ #{npcId} — {nu.Name}", "", "Total : " + UsageLine(nu.Total), "" };
            foreach (var kv in nu.ByProvider) lines.Add($"{kv.Key} : {UsageLine(kv.Value)}");
            lines.Add("");
            lines.Add("↓ = tokens en entrée, ↑ = tokens en sortie.");
            Info(player, "Consommation du PNJ", string.Join("\n", lines), () => UsageMenu(player));
        }

        // ==================================================================
        //  Réglages
        // ==================================================================

        void SettingsMenu(Player player)
        {
            if (!Guard(player)) return;
            var c = Store.Config;
            var panel = NewTab("Réglages");
            Line(panel, player, "Accès réservé aux admins", c.Admin.RequireAdmin ? "Oui" : "Non", Icon("admin"), () =>
            {
                if (c.Admin.RequireAdmin)
                {
                    Confirm(player, "Accès", "Autoriser TOUS les joueurs à ouvrir l'éditeur des PNJ ?", () =>
                    {
                        c.Admin.RequireAdmin = false;
                        Store.SaveConfig();
                        SettingsMenu(player);
                    }, () => SettingsMenu(player));
                    return;
                }
                c.Admin.RequireAdmin = true;
                Store.SaveConfig();
                SettingsMenu(player);
            });
            Line(panel, player, "Niveau admin requis", c.Admin.RequiredAdminLevel.ToString(), Icon("admin"), () =>
                AskNumber(player, "Niveau admin", "Niveau d'administration minimum pour utiliser l'éditeur.", c.Admin.RequiredAdminLevel, 0f, 100f, v =>
                {
                    int level = Mathf.RoundToInt(v);
                    if (player.account != null && level > player.account.AdminLevel)
                    {
                        player.Notify(Title, $"Impossible : vous perdriez l'accès (votre niveau : {player.account.AdminLevel}).", NotificationManager.Type.Error);
                        SettingsMenu(player);
                        return;
                    }
                    c.Admin.RequiredAdminLevel = level;
                    Store.SaveConfig();
                    SettingsMenu(player);
                }, () => SettingsMenu(player)));
            Line(panel, player, "Service admin obligatoire", c.Admin.RequireAdminService ? "Oui" : "Non", Icon("admin"), () =>
            {
                c.Admin.RequireAdminService = !c.Admin.RequireAdminService;
                Store.SaveConfig();
                SettingsMenu(player);
            });
            Line(panel, player, "Portée de discussion (nouveaux PNJ)", Fmt(c.Ai.DefaultTalkRange) + " m", Icon("range"), () =>
                AskNumber(player, "Portée de discussion", "Valeur par défaut pour les nouveaux PNJ.", c.Ai.DefaultTalkRange, 1f, 50f,
                    v => { c.Ai.DefaultTalkRange = v; Store.SaveConfig(); SettingsMenu(player); }, () => SettingsMenu(player)));
            Line(panel, player, "Portée d'écoute (nouveaux PNJ)", Fmt(c.Ai.DefaultHearRange) + " m", Icon("range"), () =>
                AskNumber(player, "Portée d'écoute", "Valeur par défaut pour les nouveaux PNJ.", c.Ai.DefaultHearRange, 1f, 100f,
                    v => { c.Ai.DefaultHearRange = v; Store.SaveConfig(); SettingsMenu(player); }, () => SettingsMenu(player)));
            Line(panel, player, "Mémoire par joueur", c.Ai.MaxHistoryMessages + " messages", Icon("memory"), () =>
                AskNumber(player, "Mémoire", "Nombre de messages gardés par joueur et par PNJ (0 = aucune mémoire).", c.Ai.MaxHistoryMessages, 0f, 100f,
                    v => { c.Ai.MaxHistoryMessages = Mathf.RoundToInt(v); Store.SaveConfig(); SettingsMenu(player); }, () => SettingsMenu(player)));
            Line(panel, player, "Délai entre deux /say", Fmt(c.Ai.SayCooldownSeconds) + " s", Icon("wait"), () =>
                AskNumber(player, "Délai /say", "Temps minimum entre deux messages d'un même joueur.", c.Ai.SayCooldownSeconds, 0f, 60f,
                    v => { c.Ai.SayCooldownSeconds = v; Store.SaveConfig(); SettingsMenu(player); }, () => SettingsMenu(player)));
            Line(panel, player, "Instructions communes de l'IA", Short(c.Ai.GlobalPrompt, 25), Icon("prompt"), () =>
                AskText(player, "Instructions communes",
                    "Règles données à tous les PNJ ({name} = nom du PNJ).\n\nActuelles : " + Short(c.Ai.GlobalPrompt, 400),
                    "Tu es {name}…", value =>
                    {
                        if (value.Length > 0)
                        {
                            c.Ai.GlobalPrompt = value.Length > 4000 ? value.Substring(0, 4000) : value;
                            Store.SaveConfig();
                        }
                        SettingsMenu(player);
                    }, () => SettingsMenu(player)));
            Line(panel, player, "Couleur des noms", $"<color={c.General.NameColor}>{c.General.NameColor}</color>", Icon("color"), () =>
                AskText(player, "Couleur des noms", "Couleur hexadécimale du nom des PNJ (ex. #f7af57).", "#f7af57", value =>
                {
                    if (ColorUtility.TryParseHtmlString(value, out Color color) && value.StartsWith("#"))
                    {
                        c.General.NameColor = value;
                        Store.SaveConfig();
                        foreach (var inst in Npcs.Instances.Values)
                            if (inst.Alive) NpcManager.SetSyncVar(inst.Setup, "NetworkusernameColor", color);
                    }
                    else player.Notify(Title, "Couleur invalide.", NotificationManager.Type.Error);
                    SettingsMenu(player);
                }, () => SettingsMenu(player)));
            Show(player, panel, () => Main(player));
        }
    }
}
