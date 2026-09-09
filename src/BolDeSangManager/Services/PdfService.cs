using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Helpers;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Drawing;

namespace BolDeSangManager.Services;

public class PdfService
{
    // NotoSansSymbols (Symbols1) : ⚕ U+2695 — Miscellaneous Symbols
    // NotoSansSymbols2          : ✦ U+2726 (Dingbats) + ★ U+2605 (Misc Symbols)
    // Deux fontes embarquées → fonctionne identiquement sur Windows (dev) et Linux (Docker).
    private const string SymbolFont1 = "BDS-Symbols1";
    private const string SymbolFont2 = "BDS-Symbols2";

    static PdfService()
    {
        var asm = typeof(PdfService).Assembly;
        using var s1 = asm.GetManifestResourceStream("BolDeSangManager.Resources.NotoSansSymbols-Regular.ttf")!;
        FontManager.RegisterFontWithCustomName(SymbolFont1, s1);
        using var s2 = asm.GetManifestResourceStream("BolDeSangManager.Resources.NotoSansSymbols2-Regular.ttf")!;
        FontManager.RegisterFontWithCustomName(SymbolFont2, s2);
    }

    /// <summary>
    /// Intitulé d'une règle spéciale sur la feuille. Pour « Favori de… » on
    /// imprime la divinité RETENUE par cette équipe (« Favori de Khorne »),
    /// pas le nom générique : c'est l'information utile à la table.
    /// </summary>
    private static string TitreRegle(TeamTypeSpecialRule lien, Team equipe) =>
        lien.SpecialRule.Code == SpecialRuleCodes.FavoriDe
        && !string.IsNullOrEmpty(equipe.DiviniteChoisie)
            ? $"Favori de {equipe.DiviniteChoisie}"
            : lien.SpecialRule.Nom;

    /// <summary>
    /// Rend une description de compétence « fluide », c'est-à-dire capable
    /// d'occuper toute la largeur disponible.
    ///
    /// ⚠️ Les descriptions importées du livre de règles contiennent des sauts de
    /// ligne HÉRITÉS de sa mise en page en colonne étroite (« Esquive » en a 4,
    /// « Minus » 7). QuestPDF les respecte à la lettre : le texte se coupait donc
    /// vers le tiers de la page en laissant les deux tiers droits vides, quelle
    /// que soit la largeur accordée au bloc. Le symptôme ressemblait à un défaut
    /// de mise en page, la cause était dans la DONNÉE.
    ///
    /// On neutralise les sauts SIMPLES (mise en page d'origine) et on préserve
    /// les sauts DOUBLES, qui séparent de vrais paragraphes — certaines
    /// compétences longues en ont, et tout aplatir collerait leurs alinéas.
    /// </summary>
    public static string TexteFluide(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;

        var normalise = description.Replace("\r\n", "\n").Replace('\r', '\n');

        // Marqueur temporaire : les vrais paragraphes doivent survivre au collage.
        const string paragraphe = "\u0001";
        normalise = System.Text.RegularExpressions.Regex.Replace(
            normalise, @"\n[ \t]*\n[\s]*", paragraphe);

        // Sauts restants = mise en page d'origine : ils redeviennent des espaces.
        normalise = normalise.Replace('\n', ' ');

        // Espaces multiples issus du collage.
        normalise = System.Text.RegularExpressions.Regex.Replace(normalise, "[ \t]{2,}", " ");

        return normalise.Replace(paragraphe, "\n").Trim();
    }

    /// <param name="paysage">
    /// Orientation du PDF (#4). En paysage la largeur utile passe de ~180 à ~267 mm :
    /// les colonnes à largeur fixe (caractéristiques, PSP, valeur) ne bougent pas,
    /// tout l'espace gagné va aux colonnes relatives — surtout les compétences.
    /// </param>
    /// <param name="coupsDePouce">
    /// Coups de pouce à imprimer, ou <c>null</c> pour ne pas les inclure.
    /// </param>
    /// <param name="starPlayers">
    /// Star players accessibles à cette équipe (recoupement déjà fait par
    /// l'appelant), ou <c>null</c> pour ne pas les inclure.
    /// </param>
    public byte[] GenererFeuilleEquipe(Team equipe, bool inclureDescriptionsCompetences,
        Match? matchProchain = null, string? urlExterne = null, bool paysage = false,
        List<Inducement>? coupsDePouce = null, List<StarPlayer>? starPlayers = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(paysage ? PageSizes.A4.Landscape() : PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Arial"));

                // ── En-tête ─────────────────────────────────────────────
                // ⚠️ QuestPDF répète page.Header() sur CHAQUE page. Le bandeau
                // complet (nom d'équipe en corps 20, race, coach, ligue, QR code)
                // mangeait donc ~2 cm en haut de chaque page suivante pour une
                // information déjà lue. On le rend uniquement sur la première ;
                // le nom d'équipe reste rappelé discrètement en pied de page,
                // qui coûte une ligne de 7 pt au lieu d'un bandeau.
                page.Header().ShowOnce().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(inner =>
                        {
                            inner.Item().Text(equipe.Nom)
                                .Bold().FontSize(20).FontColor(Colors.Red.Darken2);
                            // Race/collège, et la catégorie LRB quand elle est
                            // renseignée (0 = pas encore saisie par les commissaires).
                            var categorie = DisplayHelpers.CategorieLabel(equipe.TeamType?.Categorie ?? 0);
                            var sousTitre = $"{equipe.TeamType?.Nom} — {equipe.TeamType?.Game?.Nom}"
                                + (categorie.Length > 0 ? $" — {categorie}" : "");
                            inner.Item().Text(sousTitre)
                                .FontSize(11).FontColor(Colors.Grey.Darken2);
                        });
                        row.ConstantItem(160).Column(inner =>
                        {
                            inner.Item().Text($"Coach : {DisplayHelpers.NomCoach(equipe.Coach)}")
                                .FontSize(10);
                            inner.Item().Text($"Ligue : {equipe.League?.Nom}").FontSize(10);
                        });

                        // QR code compact en haut à droite si match à venir
                        if (matchProchain is not null && !string.IsNullOrWhiteSpace(urlExterne))
                        {
                            var url = $"{urlExterne.TrimEnd('/')}/matchs/{matchProchain.Id}/feuille";
                            var adversaire = matchProchain.EquipeDomicileId == equipe.Id
                                ? matchProchain.EquipeExterieur?.Nom ?? "Adversaire"
                                : matchProchain.EquipeDomicile?.Nom ?? "Adversaire";
                            var ronde = matchProchain.Ronde >= 100
                                ? $"Play-off T{matchProchain.Ronde - 99}"
                                : $"R{matchProchain.Ronde}";
                            var qrBytes = GenererQrCode(url);

                            row.ConstantItem(80).Border(0.5f).BorderColor(Colors.Red.Lighten2)
                                .Background(Colors.Red.Lighten5)
                                .Padding(4).Column(inner =>
                                {
                                    inner.Item().AlignCenter().Height(52).Image(qrBytes).FitArea();
                                    inner.Item().PaddingTop(2).AlignCenter()
                                        .Text($"vs {adversaire}")
                                        .FontSize(6).Bold().FontColor(Colors.Red.Darken2);
                                    inner.Item().AlignCenter()
                                        .Text(ronde)
                                        .FontSize(6).FontColor(Colors.Grey.Darken2);
                                });
                        }
                    });
                    col.Item().PaddingTop(4).LineHorizontal(1.5f).LineColor(Colors.Red.Darken2);
                });

                page.Content().Column(col =>
                {
                    // ── Stats de l'équipe ────────────────────────────────
                    // VEA : source UNIQUE (Helpers/VeaCalculator), la même que
                    // celle affichée à l'écran. Ce bloc la recalculait autrefois
                    // depuis les colonnes de staff HISTORIQUES de Team, qui sont
                    // à zéro sur toute équipe moderne : la feuille imprimée
                    // annonçait une VEA amputée de tout le staff.
                    // Barème résolu UNE fois : il sert à la VEA comme à la
                    // colonne « Valeur » de chaque joueur, qui sont calculées et
                    // doivent donc partager exactement la même base.
                    var bareme = ValeurJoueurCalculator.BaremeDe(equipe);
                    var vea = VeaCalculator.Calculer(equipe, bareme);

                    // Cases : trésorerie, VEA, puis le staff réellement détenu
                    // (liste ouverte — un staff créé par l'association apparaît
                    // sans modification de code), puis les stats de ligue.
                    var cases = new List<(string, string)>
                    {
                        ("Trésorerie", $"{equipe.Tresorerie / 1000}k po"),
                        ("VEA",        $"{vea / 1000}k po"),
                    };
                    cases.AddRange(StaffItems(equipe));
                    cases.Add(("Matchs joués", equipe.NombreMatchsJoues.ToString()));
                    cases.Add(("Points ligue", equipe.PointsLigue.ToString()));

                    StatBoxes(col, cases, paysage);

                    // ── Tableau des joueurs ──────────────────────────────
                    var joueurs = equipe.Joueurs
                        .Where(j => !j.EstMort && !j.EstRetraite)
                        .OrderBy(j => j.Numero)
                        .ToList();

                    col.Item().PaddingTop(4).Table(table =>
                    {
                        table.ColumnsDefinition(cols =>
                        {
                            cols.ConstantColumn(22);   // #
                            cols.RelativeColumn(3);    // Nom
                            cols.RelativeColumn(3);    // Poste
                            cols.ConstantColumn(22);   // M
                            cols.ConstantColumn(22);   // F
                            cols.ConstantColumn(22);   // AG
                            cols.ConstantColumn(22);   // CP
                            cols.ConstantColumn(22);   // AR
                            cols.ConstantColumn(30);   // PSP
                            cols.ConstantColumn(38);   // Valeur
                            cols.RelativeColumn(4);    // Compétences
                        });

                        // En-tête
                        table.Header(header =>
                        {
                            foreach (var h in new[] { "#", "Nom", "Poste", "M", "F", "AG", "CP", "AR", "PSP", "Valeur", "Compétences" })
                            {
                                header.Cell()
                                    .Background(Colors.Red.Darken2)
                                    .BorderBottom(2).BorderColor(Colors.Red.Darken4)
                                    .PaddingVertical(6).PaddingHorizontal(4)
                                    .Text(h).FontColor(Colors.White).Bold().FontSize(8);
                            }
                        });

                        // Lignes joueurs
                        bool pair = false;
                        foreach (var joueur in joueurs)
                        {
                            var bg  = pair ? Colors.Grey.Lighten3 : Colors.White;
                            var pos = joueur.PlayerPosition;

                            var competences = string.Join(", ",
                                (pos?.CompetencesDepart.Select(c => c.Skill?.Nom ?? "") ?? [])
                                .Concat(joueur.Competences.Where(c => !c.EstCompetenceDepart).Select(c => c.Skill?.Nom ?? ""))
                                // Compétence accordée par le titre de capitaine :
                                // calculée, jamais stockée — sans cette ligne elle
                                // manquerait sur la feuille imprimée alors qu'elle
                                // s'applique bel et bien en match.
                                .Concat(CapitaineHelper.CompetenceOfferte(joueur, equipe.TeamType) is { } cap
                                    ? [$"{cap} (capitaine)"] : Array.Empty<string>())
                                .Where(s => s != ""));

                            bool hasSequel = joueur.Blessures.Any(b =>
                                b.Type == InjuryType.BlessurePersistante);

                            Cell(table, bg, joueur.Numero.ToString(), center: true);
                            CellNom(table, bg, joueur.Nom, joueur.ManqueSuivantMatch, hasSequel);
                            // Poste + mots-clés
                            {
                                var cell = table.Cell()
                                    .Background(bg)
                                    .BorderBottom(1f).BorderColor(Colors.Grey.Lighten1)
                                    .PaddingVertical(4).PaddingHorizontal(4);
                                cell.Column(col2 =>
                                {
                                    col2.Item().Text(pos?.Nom ?? "—").FontSize(8);
                                    var mc = pos?.MotsCles ?? "";
                                    if (!string.IsNullOrEmpty(mc))
                                        col2.Item().Text(mc).FontSize(6).FontColor(Colors.Grey.Darken1).Italic();
                                });
                            }
                            CellStat(table, bg, ((pos?.Mouvement ?? 0) + joueur.ModMouvement).ToString(), joueur.ModMouvement);
                            CellStat(table, bg, ((pos?.Force    ?? 0) + joueur.ModForce).ToString(),      joueur.ModForce);
                            CellStat(table, bg, EffStatStr(pos?.Agilite       ?? "—", joueur.ModAgilite,       false), joueur.ModAgilite);
                            CellStat(table, bg, EffStatStr(pos?.CapacitePasse ?? "—", joueur.ModCapacitePasse, false), joueur.ModCapacitePasse);
                            CellStat(table, bg, EffStatStr(pos?.Armure        ?? "—", joueur.ModArmure,        true),  joueur.ModArmure);
                            CellPsp(table, bg, joueur.PointsStarPlayer);
                            Cell(table, bg, $"{ValeurJoueurCalculator.Calculer(joueur, bareme) / 1000}k", center: true);
                            Cell(table, bg, competences);

                            pair = !pair;
                        }
                    });

                    // Légende
                    col.Item().PaddingTop(4).Row(row =>
                    {
                        row.AutoItem().Text(t => IconLegend(t, "⚕", " Rate le prochain match", Colors.Grey.Darken2));
                        row.AutoItem().PaddingLeft(12).Text(t => IconLegend(t, "✦", " Séquelle persistante", Colors.Red.Darken2));
                        row.AutoItem().PaddingLeft(12).Text(t => IconLegend(t, "★", " ≥ 6 PSP : amélioration disponible", Colors.Orange.Darken3));
                        row.AutoItem().PaddingLeft(12)
                            .Background(Colors.Red.Lighten4)
                            .Padding(2)
                            .Text("Stat réduite par blessure").FontSize(7).FontColor(Colors.Red.Darken3);
                    });

                    // ── Règles spéciales de l'équipe (LRB p.93-94) ───────
                    // Les NOMS sont toujours imprimés : le coach doit savoir
                    // quelles règles s'appliquent à son équipe. Les
                    // DESCRIPTIONS suivent le même réglage que celles des
                    // compétences — la version compacte est faite pour tenir
                    // sur une page, elle ne doit pas la remplir de texte.
                    var reglesSpeciales = (equipe.TeamType?.ReglesSpecialesListe ?? [])
                        .Where(l => l.SpecialRule is not null)
                        .OrderBy(l => l.SpecialRule.Ordre).ThenBy(l => l.SpecialRule.Nom)
                        .ToList();

                    if (reglesSpeciales.Count > 0)
                    {
                        col.Item().PaddingTop(6).Text("Règles spéciales")
                            .Bold().FontSize(9).FontColor(Colors.Red.Darken2);

                        // Sans description, les noms tiennent sur une seule
                        // ligne séparés par des points : trois lignes d'une
                        // ligne chacune gaspilleraient la place gagnée.
                        if (!inclureDescriptionsCompetences)
                        {
                            var noms = reglesSpeciales.Select(l => TitreRegle(l, equipe));
                            col.Item().PaddingTop(2)
                                .Text(string.Join(" · ", noms))
                                .FontSize(7.5f).FontColor(Colors.Grey.Darken3);
                        }
                        else
                        {
                            foreach (var l in reglesSpeciales)
                            {
                                // Pleine largeur : un bloc contraint couperait le
                                // texte au tiers de la page.
                                col.Item().PaddingTop(2).Text(t =>
                                {
                                    t.Span($"{TitreRegle(l, equipe)} — ").Bold().FontSize(7.5f);
                                    t.Span(TexteFluide(l.SpecialRule.Description))
                                        .FontSize(7.5f).FontColor(Colors.Grey.Darken3);
                                });
                            }
                        }
                    }

                    // ── Rappel des compétences ───────────────────────────
                    if (inclureDescriptionsCompetences)
                    {
                        var toutesCompetences = equipe.Joueurs
                            .Where(j => !j.EstMort && !j.EstRetraite)
                            .SelectMany(j =>
                                (j.PlayerPosition?.CompetencesDepart
                                    .Select(pps => pps.Skill)
                                    .OfType<Skill>()
                                 ?? Enumerable.Empty<Skill>())
                                .Concat(j.Competences
                                    .Where(c => !c.EstCompetenceDepart)
                                    .Select(c => c.Skill)
                                    .OfType<Skill>()))
                            .DistinctBy(s => s.Id)
                            .OrderBy(s => s.Categorie)
                            .ThenBy(s => s.Nom)
                            .ToList();

                        if (toutesCompetences.Count > 0)
                        {
                            // Titre, filet et contenu dans un SEUL item : sinon QuestPDF
                            // peut couper entre eux et laisser le titre orphelin en bas
                            // de page, le rappel commençant sur la page suivante.
                            col.Item().PaddingTop(14).Column(bloc =>
                            {
                            bloc.Item().Text("Rappel des Compétences")
                                .Bold().FontSize(11).FontColor(Colors.Red.Darken2);
                            bloc.Item().PaddingTop(3).LineHorizontal(0.5f).LineColor(Colors.Red.Lighten2);

                            // Une colonne UNIQUE occupant toute la largeur, et le nom de
                            // la compétence en tête de son propre paragraphe.
                            //
                            // ⚠️ Le réglage précédent découpait ce bloc en DEUX colonnes en
                            // paysage, sur l'idée qu'« une description tient en une ligne ».
                            // C'était faux : cette impression venait des données de dev
                            // (descriptions de 40 à 120 caractères) alors que les vraies
                            // font 300 à 1000 caractères. Sur un vrai roster, deux colonnes
                            // divisent la largeur par deux et DOUBLENT donc la hauteur —
                            // l'inverse du but recherché, avec en prime une moitié droite
                            // vide dès qu'une catégorie pèse plus que toutes les autres.
                            //
                            // Le nom n'est plus dans une colonne fixe à gauche : cette
                            // colonne réservait ~110 pt à un mot de 8 caractères et retirait
                            // autant de largeur à la description sur CHAQUE ligne. En tête
                            // de paragraphe, la description récupère toute la page.
                            var parCategorie = toutesCompetences
                                .GroupBy(s => s.SkillCategoryDef?.Nom
                                              ?? s.Categorie.ToString())
                                .OrderBy(g => g.Key)
                                .Select(g => (Titre: g.Key, Skills: g.ToList()))
                                .ToList();

                            void RendreGroupes(
                                QuestPDF.Infrastructure.IContainer cible,
                                List<(string Titre, List<Skill> Skills)> groupes)
                            {
                                cible.Column(comp =>
                                {
                                    var premier = true;
                                    foreach (var (titre, skills) in groupes)
                                    {
                                        comp.Item().PaddingTop(premier ? 0 : 5)
                                            .Text(titre).Bold().FontSize(8)
                                            .FontColor(Colors.Red.Darken2);
                                        premier = false;

                                        foreach (var skill in skills)
                                        {
                                            // Nom et description dans un SEUL paragraphe :
                                            // le texte reflue sous le nom au lieu de rester
                                            // dans une colonne étroite à sa droite.
                                            comp.Item().PaddingTop(2).PaddingLeft(8)
                                                .Text(t =>
                                                {
                                                    t.Span($"{skill.Nom} — ").Bold().FontSize(8);
                                                    t.Span(TexteFluide(skill.Description)).FontSize(8)
                                                        .FontColor(Colors.Grey.Darken2);
                                                });
                                        }
                                    }
                                });
                            }

                            RendreGroupes(bloc.Item().PaddingTop(6), parCategorie);
                            });
                        }
                        else
                        {
                            col.Item().PaddingTop(10).Text("Aucune compétence à décrire.")
                                .FontSize(8).FontColor(Colors.Grey.Darken1).Italic();
                        }
                    }

                    // ── Coups de pouce ───────────────────────────────────
                    // Purement informatif : le coach compare les valeurs
                    // d'équipe et décide à la table, l'application ne débite
                    // rien.
                    //
                    // ⚠️ PAS de tableau ici, contrairement aux joueurs et aux
                    // star players. Un tableau enferme la description dans sa
                    // colonne : avec les chiffres à gauche elle ne disposait
                    // que de ~300 pt sur les 553 utiles, soit la moitié de la
                    // page perdue. Les descriptions font jusqu'à 5 600
                    // caractères — il leur faut TOUTE la largeur.
                    //
                    // D'où l'entête sur sa propre ligne (quantité, nom, coût,
                    // restriction) puis la description en dessous, sur un item
                    // pleine largeur.
                    if (coupsDePouce is { Count: > 0 })
                    {
                        // Titre et contenu dans un SEUL item : sinon QuestPDF
                        // peut laisser le titre orphelin en bas de page.
                        col.Item().PaddingTop(14).Column(bloc =>
                        {
                            bloc.Item().Text("Coups de Pouce")
                                .Bold().FontSize(11).FontColor(Colors.Red.Darken2);
                            bloc.Item().PaddingTop(3).LineHorizontal(0.5f).LineColor(Colors.Red.Lighten2);

                            foreach (var cp in coupsDePouce)
                            {
                                // Ligne d'entête : tout ce qui est court et
                                // chiffré, sur une seule ligne.
                                bloc.Item().PaddingTop(6).Text(t =>
                                {
                                    if (cp.QuantiteMax > 0)
                                        t.Span($"0-{cp.QuantiteMax}  ")
                                            .Bold().FontSize(8.5f).FontColor(Colors.Grey.Darken2);

                                    t.Span(cp.Nom).Bold().FontSize(9);

                                    t.Span(cp.Cout > 0 ? $"  —  {cp.Cout:N0} po" : "  —  prix variable")
                                        .Bold().FontSize(8.5f).FontColor(Colors.Red.Darken2);

                                    if (!string.IsNullOrWhiteSpace(cp.Restriction))
                                        t.Span($"   ({cp.Restriction})")
                                            .FontSize(7.5f).FontColor(Colors.Grey.Darken1).Italic();
                                });

                                // Description : item à part, donc pleine
                                // largeur de la zone de texte.
                                if (!string.IsNullOrWhiteSpace(cp.Description))
                                    bloc.Item().PaddingTop(1).Text(cp.Description)
                                        .FontSize(7.5f).FontColor(Colors.Grey.Darken3)
                                        .LineHeight(1.25f);
                            }
                        });
                    }

                    // ── Star players ─────────────────────────────────────
                    // Ceux que CETTE équipe peut engager : le recoupement des
                    // ligues est fait par l'appelant.
                    //
                    // Même tableau que les joueurs de l'équipe : le format en
                    // paragraphes empilait nom, stats, compétences et règle sur
                    // 4 lignes chacun, soit une bouillie de 66 blocs. En
                    // colonnes alignées, les caractéristiques se comparent d'un
                    // coup d'œil, comme sur le roster.
                    if (starPlayers is { Count: > 0 })
                    {
                        col.Item().PaddingTop(14).Column(bloc =>
                        {
                            bloc.Item().Text("Star Players disponibles")
                                .Bold().FontSize(11).FontColor(Colors.Red.Darken2);
                            bloc.Item().PaddingTop(3).LineHorizontal(0.5f).LineColor(Colors.Red.Lighten2);

                            bloc.Item().PaddingTop(4).Table(t =>
                            {
                                t.ColumnsDefinition(cols =>
                                {
                                    cols.RelativeColumn(3);    // Nom
                                    cols.ConstantColumn(22);   // M
                                    cols.ConstantColumn(22);   // F
                                    cols.ConstantColumn(22);   // AG
                                    cols.ConstantColumn(22);   // CP
                                    cols.ConstantColumn(22);   // AR
                                    cols.ConstantColumn(38);   // Coût
                                    cols.RelativeColumn(8);    // Compétences et règle
                                });

                                t.Header(h =>
                                {
                                    foreach (var titre in new[]
                                             { "Star player", "M", "F", "AG", "CP", "AR", "Coût", "Compétences et règle spéciale" })
                                    {
                                        h.Cell()
                                            .Background(Colors.Red.Darken2)
                                            .BorderBottom(2).BorderColor(Colors.Red.Darken4)
                                            .PaddingVertical(5).PaddingHorizontal(4)
                                            .Text(titre).FontColor(Colors.White).Bold().FontSize(8);
                                    }
                                });

                                var pair = false;
                                foreach (var sp in starPlayers)
                                {
                                    var bg = pair ? Colors.Grey.Lighten3 : Colors.White;

                                    Cell(t, bg, sp.Nom);
                                    Cell(t, bg, sp.Mouvement.ToString(), center: true);
                                    Cell(t, bg, sp.Force.ToString(), center: true);
                                    Cell(t, bg, sp.Agilite, center: true);
                                    Cell(t, bg, sp.CapacitePasse, center: true);
                                    Cell(t, bg, sp.Armure, center: true);
                                    Cell(t, bg, sp.Cout > 0 ? $"{sp.Cout / 1000} k" : "—", center: true);

                                    // Compétences et règle dans la MÊME cellule,
                                    // la règle en italique : deux colonnes
                                    // auraient divisé la largeur utile alors que
                                    // les textes vont jusqu'à 450 caractères.
                                    var cellule = t.Cell()
                                        .Background(bg)
                                        .BorderBottom(1f).BorderColor(Colors.Grey.Lighten1)
                                        .PaddingVertical(4).PaddingHorizontal(4);

                                    cellule.Text(txt =>
                                    {
                                        if (!string.IsNullOrWhiteSpace(sp.Competences))
                                            txt.Span(sp.Competences).FontSize(7.5f);

                                        if (!string.IsNullOrWhiteSpace(sp.ReglesSpeciales))
                                        {
                                            if (!string.IsNullOrWhiteSpace(sp.Competences))
                                                txt.Span("  ").FontSize(7.5f);
                                            txt.Span(sp.ReglesSpeciales)
                                                .FontSize(7).FontColor(Colors.Grey.Darken3).Italic();
                                        }
                                    });

                                    pair = !pair;
                                }
                            });
                        });
                    }
                });

                // ── Pied de page ─────────────────────────────────────────
                page.Footer().BorderTop(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingTop(4).Row(row =>
                {
                    // Le nom d'équipe est ici, et NON répété en tête de chaque
                    // page : une ligne de 7 pt suffit à savoir de quel roster il
                    // s'agit, là où le bandeau coûtait ~2 cm par page.
                    row.RelativeItem().Text($"{equipe.Nom} — généré le {DateTime.Now:dd/MM/yyyy HH:mm}")
                        .FontSize(7).FontColor(Colors.Grey.Darken1);
                    row.ConstantItem(60).AlignRight().Text(text =>
                    {
                        text.Span("Page ").FontSize(7);
                        text.CurrentPageNumber().FontSize(7);
                        text.Span(" / ").FontSize(7);
                        text.TotalPages().FontSize(7);
                    });
                });
            });
        }).GeneratePdf();
    }

    private static byte[] GenererQrCode(string url)
    {
        using var generator = new QRCodeGenerator();
        var qrData = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(qrData);
        return qrCode.GetGraphic(10);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Staff détenu par l'équipe, prêt à l'affichage — même logique que
    /// <c>Equipes/Detail.razor</c> (StaffItems), pour que la feuille imprimée
    /// et l'écran montrent exactement les mêmes lignes.
    ///
    /// La liste est OUVERTE : un staff créé par l'association apparaît ici sans
    /// modification de code. Repli sur les colonnes historiques pour une équipe
    /// pas encore migrée vers <c>Team.Staff</c>.
    /// </summary>
    private static IEnumerable<(string Label, string Valeur)> StaffItems(Team equipe)
    {
        var staff = equipe.Staff
            .Where(s => s.LeagueStaffType is not null)
            .OrderBy(s => s.LeagueStaffType.Ordre)
            .ThenBy(s => s.LeagueStaffType.Nom)
            .ToList();

        if (staff.Count == 0)
        {
            yield return ("Relances", equipe.NombreRelances.ToString());
            yield return ("Fans Dévoués", equipe.FansDevoues.ToString());
            yield return ("Coachs assist.", equipe.NombreCoachsAssistants.ToString());
            yield return ("Cheerleaders", equipe.NombreCheerleaders.ToString());
            yield return ("Apothicaire", equipe.Apothicaire ? "Oui" : "Non");
            yield break;
        }

        foreach (var s in staff)
        {
            // Un staff plafonné à 1 est un oui/non, pas un compteur.
            var valeur = s.LeagueStaffType.MaxCreation == 1 && s.LeagueStaffType.MaxLigue == 1
                ? (s.Quantite > 0 ? "Oui" : "Non")
                : s.Quantite.ToString();
            yield return (s.LeagueStaffType.Nom, valeur);
        }
    }

    /// <summary>
    /// Ligne(s) de cases de statistiques.
    ///
    /// ⚠️ Le staff est une liste OUVERTE : le nombre de cases n'est pas connu à
    /// l'avance. Une simple <c>Row</c> partageait la largeur entre toutes les
    /// cases, si bien qu'au-delà de ~9 entrées les libellés se coupaient en
    /// plein milieu d'un mot (« Cheerlead / ers », « Apothicair / e ») et la VEA
    /// elle-même passait à la ligne. Une grille à nombre de colonnes FIXE donne
    /// à chaque case une largeur constante et reporte le surplus sur une
    /// seconde ligne — l'ajout d'un staff maison ne peut plus casser l'en-tête.
    /// </summary>
    private static void StatBoxes(ColumnDescriptor col,
        IEnumerable<(string Label, string Valeur)> cases, bool paysage)
    {
        // A4 portrait : ~180 mm utiles / 7 = ~26 mm par case, assez pour
        // « Cheerleaders » à 7pt. Le paysage offre ~267 mm, d'où une colonne
        // de plus sans rétrécir les cases.
        var colonnes = paysage ? 9 : 7;

        col.Item().PaddingVertical(5).Grid(grid =>
        {
            grid.Columns(colonnes);
            grid.Spacing(3);

            foreach (var (label, valeur) in cases)
            {
                grid.Item()
                    .Border(0.5f).BorderColor(Colors.Grey.Lighten2)
                    .Background(Colors.Grey.Lighten5)
                    .Padding(5).Column(c =>
                    {
                        c.Item().Text(label).FontSize(7).FontColor(Colors.Grey.Darken2);
                        c.Item().Text(valeur).Bold().FontSize(10);
                    });
            }
        });
    }

    private static void Cell(TableDescriptor table, string bg, string texte, bool center = false)
    {
        var content = table.Cell()
            .Background(bg)
            .BorderBottom(1f).BorderColor(Colors.Grey.Lighten1)
            .PaddingVertical(4).PaddingHorizontal(4);

        if (center)
            content.AlignCenter().Text(texte).FontSize(8);
        else
            content.Text(texte).FontSize(8);
    }

    private static void CellNom(TableDescriptor table, string bg, string nom, bool mnm, bool sequel)
    {
        table.Cell()
            .Background(sequel ? Colors.Red.Lighten5 : bg)
            .BorderBottom(1f).BorderColor(Colors.Grey.Lighten1)
            .PaddingVertical(4).PaddingHorizontal(4)
            .Text(text =>
            {
                text.Span(nom).Bold().FontSize(8);
                if (mnm)
                    SymbolSpan(text, " ⚕", 7, Colors.Orange.Darken3, SymbolFont1);
                if (sequel)
                    SymbolSpan(text, " ✦", 7, Colors.Red.Darken2, SymbolFont2);
            });
    }

    private static void CellStat(TableDescriptor table, string bg, string texte, int mod)
    {
        string effectiveBg = mod < 0 ? Colors.Red.Lighten4 : bg;
        string textColor   = mod < 0 ? Colors.Red.Darken3  : Colors.Grey.Darken4;
        table.Cell()
            .Background(effectiveBg)
            .BorderBottom(1f).BorderColor(Colors.Grey.Lighten1)
            .PaddingVertical(4).PaddingHorizontal(4)
            .AlignCenter()
            .Text(texte).FontSize(8)
            .FontColor(textColor);
    }

    private static string EffStatStr(string baseVal, int mod, bool plusEstMieux)
    {
        if (baseVal == "—" || baseVal == "-" || mod == 0) return baseVal;
        if (!int.TryParse(baseVal.TrimEnd('+'), out var n)) return baseVal;
        var effective = plusEstMieux ? n + mod : n - mod;
        var hasSuffix = baseVal.EndsWith('+');
        return hasSuffix ? $"{effective}+" : effective.ToString();
    }

    private static void CellPsp(TableDescriptor table, string bg, int psp)
    {
        table.Cell()
            .Background(bg)
            .BorderBottom(1f).BorderColor(Colors.Grey.Lighten1)
            .PaddingVertical(4).PaddingHorizontal(4)
            .Text(text =>
            {
                text.AlignCenter();
                text.Span(psp.ToString()).FontSize(8);
                if (psp >= 6)
                    SymbolSpan(text, " ★", 7, Colors.Orange.Darken3, SymbolFont2);
            });
    }

    private static void SymbolSpan(TextDescriptor t, string symbol, float size, string color, string font)
    {
        t.Span(symbol).FontFamily(font).FontSize(size).FontColor(color);
    }

    // ⚕ U+2695 → Symbols1 ; ✦ U+2726 + ★ U+2605 → Symbols2
    private static string FontFor(string symbol) =>
        symbol.Contains('⚕') ? SymbolFont1 : SymbolFont2;

    // ══════════════════════════════════════════════════════════════════════════
    //  Règlement de ligue (R5)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Génère le PDF du règlement d'une ligue à partir de son markdown.
    ///
    /// QuestPDF ne consomme pas de markdown : on interprète ici un sous-ensemble
    /// volontairement restreint et documenté — titres (#, ##, ###), paragraphes,
    /// listes à puces et numérotées, gras (**), italique (*), citations (&gt;),
    /// séparateurs (---). Tout le reste est rendu comme du texte simple, ce qui
    /// évite qu'un markdown riche fasse dérailler la mise en page.
    /// </summary>
    public byte[] GenererReglement(League ligue)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var blocs = AnalyserMarkdown(ligue.Reglement ?? string.Empty);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Arial"));

                page.Header().Column(col =>
                {
                    col.Item().Text("Règlement").Bold().FontSize(22).FontColor(Colors.Red.Darken2);
                    col.Item().Text(ligue.Nom).FontSize(12).FontColor(Colors.Grey.Darken2);
                    col.Item().PaddingTop(4).LineHorizontal(1.5f).LineColor(Colors.Red.Darken2);
                    col.Item().PaddingBottom(10);
                });

                page.Content().Column(col =>
                {
                    if (blocs.Count == 0)
                    {
                        col.Item().PaddingTop(20).Text("Aucun règlement n'a encore été rédigé pour cette ligue.")
                            .Italic().FontColor(Colors.Grey.Darken1);
                        return;
                    }

                    foreach (var bloc in blocs)
                        RendreBloc(col, bloc);
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text($"Généré le {DateTime.Now:dd/MM/yyyy HH:mm} — BolDeSangManager")
                        .FontSize(7).FontColor(Colors.Grey.Darken1);
                    row.ConstantItem(80).AlignRight().Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                        t.Span("Page ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            });
        }).GeneratePdf();
    }

    private enum TypeBloc { Titre1, Titre2, Titre3, Paragraphe, Puce, Numero, Citation, Separateur }

    /// <param name="Marqueur">Numéro d'une liste ordonnée (« 1. »), sinon vide.</param>
    private record BlocMarkdown(TypeBloc Type, string Texte, string Marqueur = "");

    /// <summary>
    /// Découpe le markdown en blocs simples. Sous-ensemble assumé : voir
    /// <see cref="GenererReglement"/>.
    /// </summary>
    private static List<BlocMarkdown> AnalyserMarkdown(string markdown)
    {
        var blocs = new List<BlocMarkdown>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocs;

        var paragraphe = new System.Text.StringBuilder();

        void ViderParagraphe()
        {
            if (paragraphe.Length == 0) return;
            blocs.Add(new BlocMarkdown(TypeBloc.Paragraphe, paragraphe.ToString().Trim()));
            paragraphe.Clear();
        }

        foreach (var ligneBrute in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var ligne = ligneBrute.TrimEnd();

            if (string.IsNullOrWhiteSpace(ligne)) { ViderParagraphe(); continue; }

            if (ligne.StartsWith("### ")) { ViderParagraphe(); blocs.Add(new(TypeBloc.Titre3, ligne[4..].Trim())); }
            else if (ligne.StartsWith("## ")) { ViderParagraphe(); blocs.Add(new(TypeBloc.Titre2, ligne[3..].Trim())); }
            else if (ligne.StartsWith("# ")) { ViderParagraphe(); blocs.Add(new(TypeBloc.Titre1, ligne[2..].Trim())); }
            else if (ligne.TrimStart().StartsWith("- ") || ligne.TrimStart().StartsWith("* "))
            {
                ViderParagraphe();
                blocs.Add(new(TypeBloc.Puce, ligne.TrimStart()[2..].Trim()));
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(ligne.TrimStart(), @"^\d+[\.\)]\s"))
            {
                ViderParagraphe();
                var t = ligne.TrimStart();
                var num = System.Text.RegularExpressions.Regex.Match(t, @"^(\d+)[\.\)]").Groups[1].Value;
                blocs.Add(new(TypeBloc.Numero,
                    System.Text.RegularExpressions.Regex.Replace(t, @"^\d+[\.\)]\s*", ""),
                    $"{num}."));
            }
            else if (ligne.StartsWith("> "))
            {
                ViderParagraphe();
                var texte = ligne[2..].Trim();
                // une citation sur plusieurs lignes forme un seul bloc
                if (blocs.Count > 0 && blocs[^1].Type == TypeBloc.Citation)
                    blocs[^1] = blocs[^1] with { Texte = blocs[^1].Texte + " " + texte };
                else
                    blocs.Add(new(TypeBloc.Citation, texte));
            }
            else if (ligne.Trim() is "---" or "***" or "___") { ViderParagraphe(); blocs.Add(new(TypeBloc.Separateur, "")); }
            else
            {
                if (paragraphe.Length > 0) paragraphe.Append(' ');
                paragraphe.Append(ligne.Trim());
            }
        }
        ViderParagraphe();
        return blocs;
    }

    private static void RendreBloc(ColumnDescriptor col, BlocMarkdown bloc)
    {
        switch (bloc.Type)
        {
            case TypeBloc.Titre1:
                col.Item().PaddingTop(12).PaddingBottom(4)
                   .Text(t => RendreInline(t, bloc.Texte, 16, true, Colors.Red.Darken2));
                break;

            case TypeBloc.Titre2:
                col.Item().PaddingTop(10).PaddingBottom(3)
                   .Text(t => RendreInline(t, bloc.Texte, 13, true, Colors.Grey.Darken4));
                break;

            case TypeBloc.Titre3:
                col.Item().PaddingTop(8).PaddingBottom(2)
                   .Text(t => RendreInline(t, bloc.Texte, 11, true, Colors.Grey.Darken3));
                break;

            case TypeBloc.Paragraphe:
                col.Item().PaddingBottom(5)
                   .Text(t => RendreInline(t, bloc.Texte, 10, false, Colors.Black));
                break;

            case TypeBloc.Puce:
                col.Item().PaddingLeft(12).PaddingBottom(2).Row(row =>
                {
                    row.ConstantItem(12).Text("•").FontSize(10);
                    row.RelativeItem().Text(t => RendreInline(t, bloc.Texte, 10, false, Colors.Black));
                });
                break;

            case TypeBloc.Numero:
                col.Item().PaddingLeft(12).PaddingBottom(2).Row(row =>
                {
                    row.ConstantItem(18).Text(bloc.Marqueur).FontSize(10);
                    row.RelativeItem().Text(t => RendreInline(t, bloc.Texte, 10, false, Colors.Black));
                });
                break;

            case TypeBloc.Citation:
                col.Item().PaddingBottom(5).PaddingLeft(8)
                   .BorderLeft(3).BorderColor(Colors.Grey.Lighten1)
                   .PaddingLeft(8).PaddingVertical(3)
                   .Text(t => RendreInline(t, bloc.Texte, 10, false, Colors.Grey.Darken2));
                break;

            case TypeBloc.Separateur:
                col.Item().PaddingVertical(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                break;
        }
    }

    /// <summary>Gère le gras (**texte**) et l'italique (*texte*) dans une ligne.</summary>
    private static void RendreInline(TextDescriptor t, string texte, float taille, bool gras, string couleur)
    {
        t.DefaultTextStyle(s =>
        {
            var style = s.FontSize(taille).FontColor(couleur);
            return gras ? style.Bold() : style;
        });

        // **gras** puis *italique*
        var morceaux = System.Text.RegularExpressions.Regex.Split(texte, @"(\*\*[^*]+\*\*|\*[^*]+\*)");
        foreach (var m in morceaux)
        {
            if (string.IsNullOrEmpty(m)) continue;

            if (m.StartsWith("**") && m.EndsWith("**") && m.Length > 4)
                t.Span(m[2..^2]).Bold();
            else if (m.StartsWith('*') && m.EndsWith('*') && m.Length > 2)
                t.Span(m[1..^1]).Italic();
            else
                t.Span(m);
        }
    }

    private static void IconLegend(TextDescriptor t, string icon, string label, string color)
    {
        SymbolSpan(t, icon, 7, color, FontFor(icon));
        t.Span(label).FontSize(7).FontColor(color);
    }
}