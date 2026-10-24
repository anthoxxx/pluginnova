# Trash by Loris

## 🗑️ Présentation

**Trash by Loris** permet aux joueurs de jeter leurs objets dans une poubelle : une poubelle **posée par un joueur**, ou une des **poubelles déjà présentes sur la map**.

Quand un joueur est à côté d'une poubelle, il l'ouvre depuis AAMenu, choisit l'objet et la quantité, et c'est fini :

- l'objet est **retiré de son inventaire** ;
- il n'est **stocké nulle part** (ni coffre ni stockage caché) ;
- **personne ne peut le récupérer**, pas même un admin.

## ✨ Fonctionnalités

| | |
|---|---|
| 🗑️ **Poubelles posées par les joueurs** | N'importe quel joueur peut poser une poubelle et l'utiliser. |
| 🏙️ **Poubelles de la map** | Toutes les poubelles déjà placées sur la map fonctionnent aussi. |
| 📍 **Menus AAMenu** | Accessible depuis **Proximité** (poubelles posées) et **Interactions** (toutes les poubelles). |
| 🎒 **Menu inventaire** | Le joueur voit son inventaire avec les icônes et les quantités. |
| 🔢 **Quantité au choix** | Jeter une partie de ses objets, ou **Tout jeter** d'un coup. |
| ⚠️ **Avertissement** | Le joueur est prévenu que l'objet sera détruit avant de valider. |
| 🔥 **Destruction définitive** | Les objets jetés sont effacés, impossible de les récupérer. |
| 📏 **Vérification de distance** | Impossible de jeter un objet si on s'est éloigné de la poubelle. |

## 🧱 Poubelles reconnues

- **Poubelles posées** : les objets avec les ID `70`, `1087`, `1276`, `1277`, `1364`, `1365`.
- **Poubelles de la map** : toutes celles déjà présentes sur la map (celles du métier d'éboueur).
  Une poubelle ramassée par un éboueur ne peut pas être utilisée tant qu'elle n'est pas revenue.

## 📦 Installation

1. Vérifie que **ModKit** et **AAMenu** sont installés sur ton serveur.
2. Copie **`TrashByLoris.dll`** dans le dossier **`Plugins`** du serveur.
3. Redémarre le serveur.

C'est tout, il n'y a pas de fichier de configuration à remplir.

## 🎮 Utilisation

1. Va à côté d'une **poubelle de la map**, ou **pose ta poubelle** (un des objets listés ci-dessus).
2. Place-toi **à côté** de la poubelle (moins de 3 mètres).
3. Ouvre AAMenu :
   - **Interactions → Poubelle** : marche avec toutes les poubelles ;
   - ou **Proximité → Poubelle - Jeter des objets** : pour les poubelles posées.
4. Choisis l'objet à jeter dans ton inventaire → **Jeter**.
5. Indique la quantité → **Jeter** (ou **Tout jeter**).
6. L'objet est détruit. ⚠️ **Il ne pourra pas être récupéré.**

## ⚖️ Licence

© 2026 Loris. Tous droits réservés.

Tu peux utiliser ce plugin sur **ton propre serveur**. Il est **interdit** de le revendre,
de le redistribuer, de le partager, de le republier sous un autre nom, de retirer les crédits
ou de le modifier pour le diffuser.

Conditions complètes : [`LICENSE.md`](LICENSE.md)
