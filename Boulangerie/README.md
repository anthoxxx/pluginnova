# Boulangerie — by Loriss Strange

Plugin Nova-Life qui crée le **métier de boulanger** : une **caisse enregistreuse** (point
bleu côté boulanger), un **point de paiement client** (point bleu côté client), une **carte** consultable par les
clients (point bleu : produits, prix, stock, formules), un **stock** par produit, une carte de **produits** et de **formules maison** entièrement
configurable depuis AAMenu, un **paiement en espèces ou par carte** par le client, et un
journal des ventes sur **Discord** (webhook).

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `Boulangerie/libs/`.
2. Ouvrir **`Boulangerie.csproj`** directement (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/Boulangerie.dll` dans le dossier `Plugins` du serveur.

## Mise en place (staff, en service admin)
1. **Créer la caisse** : AAMenu → **Administration → Points → Caisse boulangerie**
   (ou `/caisses`) → **Nouveau modèle** → nom (ex. « Caisse comptoir ») → choisir
   l'**entreprise** de la boulangerie.
2. Choisir le modèle puis **Placer ici** : un point bleu apparaît à votre position.
   Vous pouvez en placer plusieurs (plusieurs caisses dans la même boulangerie).
3. **Créer le point de paiement client** : AAMenu → **Administration → Points → Paiement
   client boulangerie** (ou `/paiements`) → **Nouveau modèle** → nom (ex. « Paiement
   client ») → entreprise → **Placer ici**, côté client du comptoir.
4. **Créer la carte** (menu pour les clients) : AAMenu → **Administration → Points → Carte
   boulangerie** (ou `/cartes`) → **Nouveau modèle** → nom → entreprise → **Placer ici**,
   par exemple à l'entrée ou devant la vitrine.
5. **Configurer le plugin** : AAMenu → **Administration → Plugins → Boulangerie**
   (ou `/boulangerie`).

Dans **Modèles** : renommer une caisse, changer son entreprise, lui donner un **webhook
Discord à elle** (sinon celui de la configuration), ou la supprimer.
Dans **Points** : se téléporter, déplacer ou supprimer une caisse placée.

## Configuration (Administration → Plugins → Boulangerie)
| Réglage | Par défaut | Rôle |
|---|---|---|
| Webhook Discord (URL) | — | Salon où chaque vente est envoyée |
| Webhook activé | Non | Active l'envoi (aussi pour les webhooks propres aux caisses) |
| Couleur des embeds | `#E8A33D` | Couleur des messages Discord |
| Tester le webhook | | Envoie un message de test |
| Distance max avec le client | 6 m | Rayon pour trouver le client à encaisser |
| Commission du boulanger | 0 % | Part de la vente versée en banque au boulanger qui encaisse, le reste va dans le compte de l'entreprise |
| Remise maximale | 30 % | Remise maximale qu'un boulanger peut faire sur un panier (0 = pas de remise) |
| Délai au point de paiement | 600 s | Temps pendant lequel une commande attend le client au point de paiement |
| Délai de l'addition directe | 90 s | Temps laissé au client pour payer une addition directe |
| Prise de service obligatoire | Oui | Il faut être en service pour utiliser la caisse |
| Donner les items au client | Oui | Le client reçoit les items liés aux produits |
| Prendre les items dans l'inventaire du boulanger | Non | Le boulanger doit avoir les produits sur lui (stock réel) |
| Les employés peuvent modifier la carte | Non | Sinon seul le patron (et le staff) |
| Gérer la carte d'une boulangerie | | Le staff peut modifier la carte de n'importe quelle boulangerie |

## La carte (patron)
AAMenu → **Métier → Boulangerie**, ou à la caisse → **Gérer la boulangerie** :
- **Produits** : *Ajouter* → nom → prix → item du jeu remis au client (choisi dans votre
  inventaire, par ID, ou aucun). *Modifier* : nom, prix, item, **en vente / rupture**, supprimer.
- **Stock / réapprovisionner** (aussi accessible à tous les employés depuis la caisse) :
  *Ajouter au stock* (+10 croissants sortis du four...), *Définir* une quantité, et pour le
  patron *Illimité / suivi*. Un produit sans suivi de stock est illimité. Un produit à 0 est
  affiché « Épuisé » et ne peut plus être ajouté au panier ; une formule dont un produit est
  épuisé est affichée « Épuisée ». Le stock baisse à chaque vente et le boulanger est
  prévenu quand un produit tombe à 0.
- **Formules maison** : *Ajouter* → nom (ex. « Petit-déjeuner ») → prix → contenu
  (+1 / −1 sur chaque produit, ex. 1 croissant + 1 café). Le prix « à l'unité » est affiché
  pour comparer.
- **Statistiques** : chiffre d'affaires du jour / 7 jours / 30 jours / total, espèces vs carte,
  meilleurs vendeurs, articles les plus vendus.
- **Historique des ventes** : les 50 derniers tickets, avec le détail de chacun.

## La carte (clients)
En marchant dans le point bleu **Carte**, le client voit tous les produits en vente avec leur
prix et leur état (*Disponible*, *En stock : 12*, *Plus que 3 !*, *Épuisé*), puis les
formules maison avec leur contenu, leur prix et l'économie par rapport aux articles à l'unité.
Le point de paiement affiche aussi la carte quand le client n'a pas de commande à payer, et
le boulanger peut l'ouvrir depuis la caisse (**Voir la carte client**).

## À la caisse (boulanger)
1. Le client demande un croissant : **+ Ajouter un produit** → *Croissant* (ou *Quantité...*).
   Pour une formule : **+ Ajouter une formule** → *Petit-déjeuner*.
2. Le panier s'affiche avec le total. Sélectionner une ligne retire 1 article.
   **Appliquer une remise** si besoin, **Vider le panier** pour recommencer.
3. Quand le client a fini : **Encaisser le client**, puis :
   - choisir le client à proximité → **Au point de paiement** : le client est prévenu que sa
     commande l'attend ;
   - ou **Commande au comptoir (client non désigné)** : le premier client qui passe au point
     de paiement la règle (pratique s'il est un peu loin) ;
   - ou choisir le client → **Addition directe** : le récapitulatif s'ouvre tout de suite chez
     le client, sans point de paiement.
4. Le client marche dans le **point bleu de paiement** : il voit le **récapitulatif** (numéro
   de commande, articles, remise, total, son argent liquide et son solde bancaire) et choisit
   **Espèces** ou **Carte bancaire**. S'il y a plusieurs commandes au comptoir en attente,
   il choisit la sienne.
5. Paiement accepté : l'argent va dans le compte de l'entreprise (moins la commission), le
   client reçoit ses items, le ticket est enregistré et envoyé sur Discord.

Tant que le client n'a pas payé, la caisse affiche la commande en attente (sélectionner
pour annuler). « Mes ventes du jour » indique le nombre et le total de vos ventes.

Les données sont dans la base ModKit (`Plugins/ModKit/data.sqlite`).
