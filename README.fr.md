# DmxCtrl — console de lumière DMX pour boîtiers Bluetooth bon marché

**Une console de lumière Windows qui pilote vos projecteurs via un boîtier Bluetooth
« USB-RDM-DMX » bon marché — sans interface DMX USB, sans câble jusqu'au PC, rien à
installer.**

🇬🇧 [English version](README.md)

![Plateforme](https://img.shields.io/badge/plateforme-Windows%2010%20%7C%2011-blue)
![.NET](https://img.shields.io/badge/.NET-10%20WinForms-purple)
![Portable](https://img.shields.io/badge/exe-portable%2C%20un%20seul%20fichier-brightgreen)
![Contributions](https://img.shields.io/badge/profils%20d'appareils-contributions%20bienvenues-orange)

<!--
  CAPTURES D'ÉCRAN — à ajouter depuis le PC Windows : mettre les PNG dans docs/images/
  et décommenter ce bloc (mêmes fichiers que le README anglais).

<p align="center">
  <img src="docs/images/show-tab.png" width="49%" alt="Onglet Soirée">
  <img src="docs/images/sweep-tab.png" width="49%" alt="Onglet Balayage">
  <img src="docs/images/laser-tab.png" width="49%" alt="Onglet Laser">
  <img src="docs/images/fog-tab.png" width="49%" alt="Onglet Fumée">
</p>
-->

DmxCtrl dialogue avec les boîtiers Bluetooth DMX vendus avec l'appli Android **RY-AI**
(puce Jieli, port série `JL_SPP`), grâce au protocole documenté dans
[JL_SPP-ProtocolLib-DMX](https://github.com/Tom-Rfid/JL_SPP-ProtocolLib-DMX). On
appaire le boîtier, on choisit son port COM, et on joue le spectacle depuis le PC.

## Fonctions

**Onglet Soirée** — tout pour une soirée au même endroit : couleurs, mouvements,
chenillard, strobe, master, blackout, flash, tap tempo, scènes et mode auto. Le
**micro** peut donner le tempo et faire suivre le volume de la musique au dimmer.

**Onglet Balayage** — les lyres (et les ailes du Spider) balaient gauche / droite
ensemble, au pad ou en automatique (délai, BPM ou tempo du micro). Mode croisé selon
la place de chaque appareil sur scène (gauche / milieu / droite), Spider en miroir,
dimmers communs ou séparés.

**Un onglet par appareil**, généré depuis son profil :

| Type d'appareil | Ce que donne l'onglet |
|---|---|
| Lyre | pad pan/tilt, une commande par canal avec les presets du profil (couleurs, gobos…) |
| Spider | pad des ailes, masque de couleurs, effet gauche/droite |
| PAR LED | groupes A/B/C, TOUT COUPER, effet au tempo |
| Laser | 32 motifs dessinés, couleurs par type (fixe, alternance, défilement), zoom, inversion X/Y |
| Machine à fumée | maintenir, coup de 0,5 à 5 s, mode auto (un jet toutes les 1 à 30 min), chauffe estimée, commandes « toutes les machines » |

**Et aussi**

- **Patch** : adresses libres proposées automatiquement, appareils désactivables,
  place sur scène, vérification de l'adressage.
- **Éditeur de profils** : décrire n'importe quel appareil DMX canal par canal,
  l'importer / l'exporter en fichier `.dmxprofile.json`.
- **Onglet Debug** : curseurs bruts, moniteur de trames, cadence d'envoi.
- Option d'envoi **seulement si changement** (+ trame de maintien).
- **Reconnexion Bluetooth automatique** toutes les 3 s si la liaison est perdue.
- Interface en **français et en anglais** (Paramètres > Langue / Language).

## Télécharger et lancer

1. Générer l'exe portable avec `publish.cmd` (voir plus bas), ou utiliser un
   `DmxController.exe` déjà généré. C'est un seul fichier de 52 Mo, .NET compris :
   **rien à installer**. Si SmartScreen avertit : *Informations complémentaires* >
   *Exécuter quand même*.
2. Appairer le boîtier Bluetooth dans Windows (Paramètres > Bluetooth, appareil `RY-…`).
3. Lancer DmxController, choisir le port COM **sortant** du boîtier (« JL_SPP ») dans
   la barre de connexion, puis **Connecter**.
4. *Fichier > Ouvrir / importer…* une des configurations d'exemple du dossier
   `Examples`, puis vérifier les adresses dans *Appareils > Patch / adressage…* par
   rapport au menu de vos appareils.

Vos configurations sont enregistrées dans `%APPDATA%\DmxController\Configurations`.

## Appareils pris en charge

Modèles intégrés (à choisir dans le patch : le nombre de canaux et une adresse libre
sont remplis tout seuls) :

| Appareil | Canaux | Adresse d'exemple |
|---|---|---|
| Chauvet DJ Intimidator Spot 260X (×2) | 14 (mode 8 canaux disponible) | 1 et 15 |
| Mindi Mini Spider 8x3W RGBW | 15 | 29 |
| PAR LED 36 UV U'King (ZQ01087) | 7 | 44 |
| Laser Showtec Galactic RGY-140 MKII (version rouge/vert disponible) | 10 | 51 |
| Machine à fumée Ayra WSM02 MKIII | 1 | 61 |

Tout autre appareil DMX s'ajoute avec l'**éditeur de profils**, sans écrire de code.

> ⚠ **Laser Galactic :** le programme « Son rouge » (valeur 195 sur le canal 1) met le
> laser en mode test « tst » : il n'écoute plus le DMX. Pour en sortir : FUNC →
> adresse DMX → ENTER sur le laser, ou l'éteindre et le rallumer. Garder le zoom à 60
> ou plus (60 par défaut) : en dessous, le motif se déforme.
>
> **Machine à fumée :** le DMX ne renvoie aucune information, l'état « prête » affiché
> est une estimation.

## Ajoutez votre appareil — les contributions sont bienvenues !

**Tout le monde est le bienvenu pour ajouter un appareil.** Deux façons, de la plus
simple à la plus complète :

1. **Un fichier de profil (sans code).** *Appareils > Éditeur de profils (modules)…* :
   créer un profil, une ligne par canal DMX dans l'ordre de l'appareil, et donner à
   chaque canal son **rôle** (dimmer, pan, rouge, gobo, strobe, fumée, motif laser…).
   C'est le rôle qui permet à l'onglet Soirée, aux balayages et à l'onglet de
   l'appareil de le piloter automatiquement. Cocher *Vérifié* sur les canaux testés,
   puis **Exporter…** le profil en fichier `.dmxprofile.json` et proposer une pull
   request qui l'ajoute dans `examples/` (ou le joindre à une issue).
2. **Un modèle intégré (C#).** Ajouter une méthode dans `Core/Model/FixtureLibrary.cs`
   à côté des existantes (`MiniSpider()`, `AyraWsm02()`, `GalacticRgy140()`…),
   l'inscrire dans `All()`, et ajouter un test dans `Tests/`. Les textes sont écrits
   en français : ce sont les clés de la table anglaise `Core/Localization/Tr.En.cs`,
   y ajouter aussi la ligne anglaise.

Merci de préciser l'appareil utilisé pour les essais et les canaux encore non
vérifiés. Les tables d'appareils utiles à d'autres programmes sont aussi les
bienvenues dans [JL_SPP-ProtocolLib-DMX](https://github.com/Tom-Rfid/JL_SPP-ProtocolLib-DMX)
sous forme de modules de librairie.

## Compiler

Prérequis : Windows, [SDK .NET 10](https://dotnet.microsoft.com/download).

```bat
dotnet build DmxController.slnx
dotnet test Tests/DmxController.Tests.csproj
dotnet run --project App
```

Exe portable : lancer `publish.cmd`. Il produit `publish\win-x64\DmxController.exe`
(un seul fichier, .NET compris) avec à côté le `README.txt` et le dossier `Examples`.

### Organisation du code

```
Core/            sans WinForms, réutilisable (portage Mac / Linux possible)
  Protocol/      brouillage, trame de 521 octets, liaison série avec thread d'envoi à 10 Hz
  Model/         profils, bibliothèque d'appareils, patch, scènes, couleurs
  Engine/        ShowEngine : calcule l'univers à chaque trame
  Audio/         détection du tempo (tempo et volume du micro)
  Localization/  traductions (clés en français, table anglaise dans Tr.En.cs)
  Storage/       fichiers de configuration et de profils
App/UI/          interface WinForms (Soirée, Balayage, onglets d'appareils, laser, fumée, patch, debug)
Tests/           tests xUnit (protocole, moteur, configuration, langues)
examples/        configurations d'exemple livrées avec l'exe
```

Les commentaires du code sont en anglais.

## Idées pour la suite

- Faire réagir le laser au tempo du micro.
- Piloter une machine à fumée sans DMX par un relais DMX sur le bouton de sa
  télécommande.
- Scan RDM, portage Mac de `Core`, contrôleur MIDI.

## Projet lié

[**JL_SPP-ProtocolLib-DMX**](https://github.com/Tom-Rfid/JL_SPP-ProtocolLib-DMX) — la
spécification du protocole et les librairies (Python, C, C#, Android) sur lesquelles
repose DmxCtrl.

## Avertissement

Projet indépendant, sans lien avec le fabricant du boîtier, avec Jieli ni avec les
marques d'appareils citées. Les lasers et machines à fumée demandent des précautions :
suivez leurs consignes de sécurité.
