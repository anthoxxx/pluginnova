# Loris Casino

**Created by Loris Strange** — plugin Nova-Life (ModKit + AAMenu).

Casino complet **100 % en panels**, sans aucune commande de chat : les joueurs passent par
les **points bleus** du casino, le staff gère tout depuis **AAMenu**.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `LorisCasino/libs/`.
2. Ouvrir **`LorisCasino.csproj`** directement (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/LorisCasino.dll` dans le dossier `Plugins` du serveur.

## Où trouver le casino dans AAMenu
| Qui | Où | Quoi |
|---|---|---|
| Admin (en service) | **Administration → Plugins → Loris Casino** (by Loris Strange) | Configuration complète |
| Admin (en service) | **Administration → Points bleus → Type de point : Loris Casino** | Créer / placer / supprimer / se téléporter aux points |
| Staff / employés | **Interactions → Loris Casino** → Espace employé | Jetons des joueurs, conversion, staff |
| Joueurs | Les **points bleus** placés au casino | Jeux, boutique, récompense… |

> Par défaut, **Interactions → Loris Casino** n'ouvre le casino que pour le staff / les employés.
> Option « Menu casino partout » dans la configuration pour l'ouvrir à tous les joueurs.

## Points bleus (types)
| Type | Ouvre |
|---|---|
| Menu principal | menu complet (jeux, boutique, récompense, classement, espace employé) |
| Menu des jeux | la liste des jeux |
| Jeu : … | directement un jeu précis (roulette, machine à sous…) |
| Boutique | achat / revente de jetons |
| Récompense quotidienne | donne la récompense du jour (1 fois par jour) |
| Espace employé | réservé au staff et aux employés de l'entreprise casino |

Placement : Points bleus → Loris Casino → **Nouveau point** (choisir le type, nommer) →
choisir le modèle → **Placer ici**. Les points sont recréés pour chaque joueur à son spawn.
**Points placés** : se téléporter, déplacer ici, supprimer.

## Jeux (mise max **500 jetons**, tirages aléatoires)
| Jeu | Règle |
|---|---|
| Pile ou Face | x2 |
| Roulette | Rouge / Noir x2 (47,5 % chacun), Vert x10 (5 %) |
| Machine à sous | 3 identiques : Cerise x3, Citron x4, Orange x5, Cloche x10, Étoile x20, 7 x50 — paire x1,5 |
| Blackjack | Tirer / Rester, banque tire jusqu'à 17 ; victoire x2, égalité mise rendue |
| Ticket à gratter | prix fixe, 1 case sur 8, gains de x0 à x4 |
| Dé - Double ou Rien | gagné sur 5 ou 6 : x2 |
| Coffre Mystère | prix fixe, 1 coffre gagnant sur 3 : +50 jetons (configurable) |
| Machine des Multiplicateurs | x1,2 → x1,5 → x2 → x3 → x5 → x10, continuer ou encaisser, risque croissant |

Chaque jeu est activable / désactivable, avec mise min / max (plafonnée à 500) ou prix.

## Jetons & économie
- Achat : € (argent liquide) → jetons, au prix configuré.
- Revente : jetons → € à X % (90 % par défaut), en boutique (désactivable) ou via un employé
  (« Convertir les jetons d'un joueur proche », le joueur doit accepter).
- Si une **entreprise casino** est configurée, l'argent des achats va sur son compte et les
  reventes sont payées par ce compte (refus si fonds insuffisants).

## Staff casino
Staff = admin en service avec l'AdminLevel requis **ou** patron de l'entreprise casino **ou**
présent dans la liste staff (base de données). Les employés de l'entreprise casino accèdent à
l'espace employé (conversion) ; ajout / retrait de jetons réservé au staff ; gestion du staff
réservée aux admins et au patron.

## Configuration (en jeu, sans fichier)
Titre des panels, AdminLevel requis, entreprise casino, valeur du jeton, % de revente,
récompense quotidienne, revente en boutique, menu partout, jeux (activation / mises / prix),
webhook Discord (activation, URL, couleur hex, test), remise à zéro du classement.

## Discord (optionnel)
Achats / reventes / conversions, gains / pertes des jeux, récompenses, actions staff,
reset hebdomadaire (avec podium). Pied d'embed : « Created by Loris Strange ».

## Stockage
Base ModKit (`Plugins/ModKit/data.sqlite`) : `CasinoConfig`, `CasinoClient`, `CasinoStaff`,
`CasinoPoint` (+ positions dans `NPoint`).
Maintenance automatique : classement hebdomadaire remis à zéro chaque lundi à 00:00.
