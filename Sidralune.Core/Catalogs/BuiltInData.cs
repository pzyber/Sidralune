namespace Sidralune.Core.Catalogs
{
    /// <summary>
    /// Small embedded catalogs so the application is useful before any catalog download.
    /// Positions J2000. Full catalogs (HYG, OpenNGC, Gaia, MPC) are downloaded through the Data Manager.
    /// </summary>
    internal static class BuiltInData
    {
        // Name|RA h m s|Dec d m s|V|B-V|Bayer|Constellation
        public const string BrightStars = @"
Sirius|06 45 08.9|-16 42 58|-1.46|0.00|Alp CMa|CMa
Canopus|06 23 57.1|-52 41 45|-0.74|0.15|Alp Car|Car
Rigil Kentaurus|14 39 36.5|-60 50 02|-0.27|0.71|Alp Cen|Cen
Arcturus|14 15 39.7|+19 10 57|-0.05|1.23|Alp Boo|Boo
Vega|18 36 56.3|+38 47 01|0.03|0.00|Alp Lyr|Lyr
Capella|05 16 41.4|+45 59 53|0.08|0.80|Alp Aur|Aur
Rigel|05 14 32.3|-08 12 06|0.13|-0.03|Bet Ori|Ori
Procyon|07 39 18.1|+05 13 30|0.34|0.42|Alp CMi|CMi
Achernar|01 37 42.8|-57 14 12|0.46|-0.16|Alp Eri|Eri
Betelgeuse|05 55 10.3|+07 24 25|0.50|1.85|Alp Ori|Ori
Hadar|14 03 49.4|-60 22 23|0.61|-0.23|Bet Cen|Cen
Altair|19 50 47.0|+08 52 06|0.76|0.22|Alp Aql|Aql
Acrux|12 26 35.9|-63 05 57|0.76|-0.24|Alp Cru|Cru
Aldebaran|04 35 55.2|+16 30 33|0.86|1.54|Alp Tau|Tau
Antares|16 29 24.5|-26 25 55|0.96|1.83|Alp Sco|Sco
Spica|13 25 11.6|-11 09 41|0.97|-0.23|Alp Vir|Vir
Pollux|07 45 18.9|+28 01 34|1.14|1.00|Bet Gem|Gem
Fomalhaut|22 57 39.0|-29 37 20|1.16|0.09|Alp PsA|PsA
Deneb|20 41 25.9|+45 16 49|1.25|0.09|Alp Cyg|Cyg
Mimosa|12 47 43.3|-59 41 19|1.25|-0.24|Bet Cru|Cru
Regulus|10 08 22.3|+11 58 02|1.40|-0.11|Alp Leo|Leo
Adhara|06 58 37.5|-28 58 20|1.50|-0.21|Eps CMa|CMa
Castor|07 34 36.0|+31 53 18|1.58|0.03|Alp Gem|Gem
Shaula|17 33 36.5|-37 06 14|1.62|-0.22|Lam Sco|Sco
Gacrux|12 31 09.9|-57 06 48|1.63|1.60|Gam Cru|Cru
Bellatrix|05 25 07.9|+06 20 59|1.64|-0.22|Gam Ori|Ori
Elnath|05 26 17.5|+28 36 27|1.65|-0.13|Bet Tau|Tau
Miaplacidus|09 13 12.0|-69 43 02|1.67|0.07|Bet Car|Car
Alnilam|05 36 12.8|-01 12 07|1.69|-0.18|Eps Ori|Ori
Alnair|22 08 14.0|-46 57 40|1.73|-0.07|Alp Gru|Gru
Alnitak|05 40 45.5|-01 56 34|1.77|-0.21|Zet Ori|Ori
Alioth|12 54 01.7|+55 57 35|1.77|-0.02|Eps UMa|UMa
Dubhe|11 03 43.7|+61 45 03|1.79|1.07|Alp UMa|UMa
Mirfak|03 24 19.4|+49 51 40|1.79|0.48|Alp Per|Per
Wezen|07 08 23.5|-26 23 36|1.83|0.68|Del CMa|CMa
Kaus Australis|18 24 10.3|-34 23 05|1.85|-0.03|Eps Sgr|Sgr
Sargas|17 37 19.1|-42 59 52|1.86|0.40|The Sco|Sco
Avior|08 22 30.8|-59 30 34|1.86|1.28|Eps Car|Car
Alkaid|13 47 32.4|+49 18 48|1.86|-0.19|Eta UMa|UMa
Menkalinan|05 59 31.7|+44 56 51|1.90|0.08|Bet Aur|Aur
Atria|16 48 39.9|-69 01 39|1.91|1.45|Alp TrA|TrA
Alhena|06 37 42.7|+16 23 57|1.93|0.00|Gam Gem|Gem
Peacock|20 25 38.9|-56 44 06|1.94|-0.20|Alp Pav|Pav
Alsephina|08 44 42.2|-54 42 32|1.96|0.04|Del Vel|Vel
Mirzam|06 22 42.0|-17 57 21|1.98|-0.23|Bet CMa|CMa
Polaris|02 31 49.1|+89 15 51|1.98|0.60|Alp UMi|UMi
Alphard|09 27 35.2|-08 39 31|1.99|1.44|Alp Hya|Hya
Hamal|02 07 10.4|+23 27 45|2.01|1.15|Alp Ari|Ari
Diphda|00 43 35.4|-17 59 12|2.04|1.02|Bet Cet|Cet
Nunki|18 55 15.9|-26 17 48|2.05|-0.13|Sig Sgr|Sgr
Menkent|14 06 41.0|-36 22 12|2.06|1.01|The Cen|Cen
Alpheratz|00 08 23.3|+29 05 26|2.06|-0.11|Alp And|And
Mirach|01 09 43.9|+35 37 14|2.07|1.58|Bet And|And
Tiaki|22 42 40.1|-46 53 05|2.07|1.60|Bet Gru|Gru
Rasalhague|17 34 56.1|+12 33 36|2.08|0.15|Alp Oph|Oph
Kochab|14 50 42.3|+74 09 20|2.08|1.47|Bet UMi|UMi
Algieba|10 19 58.4|+19 50 29|2.08|1.13|Gam Leo|Leo
Saiph|05 47 45.4|-09 40 11|2.09|-0.17|Kap Ori|Ori
Almach|02 03 54.0|+42 19 47|2.10|1.37|Gam And|And
Algol|03 08 10.1|+40 57 20|2.12|-0.05|Bet Per|Per
Denebola|11 49 03.6|+14 34 19|2.14|0.09|Bet Leo|Leo
Muhlifain|12 41 31.0|-48 57 36|2.20|-0.01|Gam Cen|Cen
Aspidiske|09 17 05.4|-59 16 31|2.21|0.18|Iot Car|Car
Suhail|09 07 59.8|-43 25 57|2.23|1.66|Lam Vel|Vel
Alphecca|15 34 41.3|+26 42 53|2.23|0.03|Alp CrB|CrB
Mizar|13 23 55.5|+54 55 31|2.23|0.02|Zet UMa|UMa
Sadr|20 22 13.7|+40 15 24|2.23|0.67|Gam Cyg|Cyg
Schedar|00 40 30.4|+56 32 14|2.24|1.17|Alp Cas|Cas
Eltanin|17 56 36.4|+51 29 20|2.24|1.52|Gam Dra|Dra
Mintaka|05 32 00.4|-00 17 57|2.25|-0.22|Del Ori|Ori
Caph|00 09 10.7|+59 08 59|2.28|0.34|Bet Cas|Cas
Dschubba|16 00 20.0|-22 37 18|2.29|-0.12|Del Sco|Sco
Larawag|16 50 09.8|-34 17 36|2.29|1.15|Eps Sco|Sco
Merak|11 01 50.5|+56 22 57|2.37|-0.02|Bet UMa|UMa
Izar|14 44 59.2|+27 04 27|2.37|0.97|Eps Boo|Boo
Enif|21 44 11.2|+09 52 30|2.39|1.53|Eps Peg|Peg
Ankaa|00 26 17.0|-42 18 22|2.40|1.09|Alp Phe|Phe
Scheat|23 03 46.5|+28 04 58|2.42|1.67|Bet Peg|Peg
Sabik|17 10 22.7|-15 43 29|2.43|0.06|Eta Oph|Oph
Phecda|11 53 49.8|+53 41 41|2.44|0.04|Gam UMa|UMa
Aludra|07 24 05.7|-29 18 11|2.45|-0.08|Eta CMa|CMa
Alderamin|21 18 34.8|+62 35 08|2.45|0.26|Alp Cep|Cep
Markeb|09 22 06.8|-55 00 39|2.47|-0.14|Kap Vel|Vel
Navi|00 56 42.5|+60 43 00|2.47|-0.15|Gam Cas|Cas
Aljanah|20 46 12.7|+33 58 13|2.48|1.03|Eps Cyg|Cyg
Markab|23 04 45.7|+15 12 19|2.49|-0.04|Alp Peg|Peg
Menkar|03 02 16.8|+04 05 23|2.54|1.64|Alp Cet|Cet
Zosma|11 14 06.5|+20 31 25|2.56|0.12|Del Leo|Leo
Arneb|05 32 43.8|-17 49 20|2.58|0.21|Alp Lep|Lep
Gienah|12 15 48.4|-17 32 31|2.59|-0.11|Gam Crv|Crv
Zubeneschamali|15 17 00.4|-09 22 59|2.61|-0.07|Bet Lib|Lib
Acrab|16 05 26.2|-19 48 19|2.62|-0.07|Bet Sco|Sco
Unukalhai|15 44 16.1|+06 25 32|2.63|1.17|Alp Ser|Ser
Sheratan|01 54 38.4|+20 48 29|2.64|0.13|Bet Ari|Ari
Ruchbah|01 25 49.0|+60 14 07|2.68|0.13|Del Cas|Cas
Tarazed|19 46 15.6|+10 36 48|2.72|1.52|Gam Aql|Aql
Porrima|12 41 39.6|-01 26 58|2.74|0.36|Gam Vir|Vir
Zubenelgenubi|14 50 52.7|-16 02 30|2.75|0.15|Alp Lib|Lib
Kornephoros|16 30 13.2|+21 29 23|2.78|0.94|Bet Her|Her
Algenib|00 13 14.2|+15 11 01|2.83|-0.23|Gam Peg|Peg
Vindemiatrix|13 02 10.6|+10 57 33|2.83|0.94|Eps Vir|Vir
Alcyone|03 47 29.1|+24 06 18|2.87|-0.09|Eta Tau|Tau
Deneb Algedi|21 47 02.4|-16 07 38|2.87|0.29|Del Cap|Cap
Sadalsuud|21 31 33.5|-05 34 16|2.90|0.83|Bet Aqr|Aqr
Cor Caroli|12 56 01.7|+38 19 06|2.90|-0.12|Alp CVn|CVn
Sadalmelik|22 05 47.0|-00 19 11|2.95|0.98|Alp Aqr|Aqr
Alnasl|18 05 48.5|-30 25 27|2.98|1.00|Gam Sgr|Sgr
Mira|02 19 20.8|-02 58 39|3.04|1.42|Omi Cet|Cet
Albireo|19 30 43.3|+27 57 35|3.08|1.09|Bet Cyg|Cyg
Errai|23 39 20.8|+77 37 57|3.21|1.03|Gam Cep|Cep
Megrez|12 15 25.6|+57 01 57|3.31|0.08|Del UMa|UMa
Rasalgethi|17 14 38.9|+14 23 25|3.35|1.16|Alp Her|Her
Thuban|14 04 23.3|+64 22 33|3.65|-0.05|Alp Dra|Dra
Alcor|13 25 13.5|+54 59 17|3.99|0.17|80 UMa|UMa
";

        // Id|Name|RA h m|Dec d m|Type|Mag|Size arcmin (maj x min)|PA|Const
        public const string DeepSky = @"
M1|Crab Nebula|05 34.5|+22 01|SNR|8.4|6x4|130|Tau
M2||21 33.5|-00 49|GC|6.5|16||Aqr
M3||13 42.2|+28 23|GC|6.2|18||CVn
M4||16 23.6|-26 32|GC|5.6|36||Sco
M5||15 18.6|+02 05|GC|5.6|23||Ser
M6|Butterfly Cluster|17 40.1|-32 13|OC|4.2|25||Sco
M7|Ptolemy Cluster|17 53.9|-34 49|OC|3.3|80||Sco
M8|Lagoon Nebula|18 03.8|-24 23|EN|6.0|90x40|90|Sgr
M9||17 19.2|-18 31|GC|7.7|12||Oph
M10||16 57.1|-04 06|GC|6.6|20||Oph
M11|Wild Duck Cluster|18 51.1|-06 16|OC|6.3|14||Sct
M12||16 47.2|-01 57|GC|6.7|16||Oph
M13|Hercules Cluster|16 41.7|+36 28|GC|5.8|20||Her
M14||17 37.6|-03 15|GC|7.6|11||Oph
M15||21 30.0|+12 10|GC|6.2|18||Peg
M16|Eagle Nebula|18 18.8|-13 47|CN|6.4|35x28||Ser
M17|Omega Nebula|18 20.8|-16 11|EN|6.0|46x37||Sgr
M18||18 19.9|-17 08|OC|7.5|9||Sgr
M19||17 02.6|-26 16|GC|6.8|17||Oph
M20|Trifid Nebula|18 02.6|-23 02|EN|6.3|28||Sgr
M21||18 04.6|-22 30|OC|6.5|13||Sgr
M22||18 36.4|-23 54|GC|5.1|32||Sgr
M23||17 56.8|-19 01|OC|6.9|27||Sgr
M24|Sagittarius Star Cloud|18 16.9|-18 29|SC|4.6|90||Sgr
M25||18 31.6|-19 15|OC|4.6|32||Sgr
M26||18 45.2|-09 24|OC|8.0|15||Sct
M27|Dumbbell Nebula|19 59.6|+22 43|PN|7.4|8x6|125|Vul
M28||18 24.5|-24 52|GC|6.8|11||Sgr
M29||20 23.9|+38 31|OC|7.1|7||Cyg
M30||21 40.4|-23 11|GC|7.2|12||Cap
M31|Andromeda Galaxy|00 42.7|+41 16|Gx|3.4|190x60|35|And
M32||00 42.7|+40 52|Gx|8.1|8x6|170|And
M33|Triangulum Galaxy|01 33.9|+30 39|Gx|5.7|71x42|23|Tri
M34||02 42.0|+42 47|OC|5.5|35||Per
M35||06 08.9|+24 20|OC|5.3|28||Gem
M36||05 36.1|+34 08|OC|6.3|12||Aur
M37||05 52.4|+32 33|OC|6.2|24||Aur
M38||05 28.7|+35 50|OC|7.4|21||Aur
M39||21 32.2|+48 26|OC|4.6|32||Cyg
M40|Winnecke 4|12 22.4|+58 05|DS|8.4|1||UMa
M41||06 46.0|-20 44|OC|4.6|38||CMa
M42|Orion Nebula|05 35.4|-05 27|EN|4.0|85x60||Ori
M43|De Mairan's Nebula|05 35.6|-05 16|EN|9.0|20x15||Ori
M44|Beehive Cluster|08 40.1|+19 59|OC|3.7|95||Cnc
M45|Pleiades|03 47.0|+24 07|OC|1.6|110||Tau
M46||07 41.8|-14 49|OC|6.1|27||Pup
M47||07 36.6|-14 30|OC|4.4|30||Pup
M48||08 13.8|-05 48|OC|5.8|54||Hya
M49||12 29.8|+08 00|Gx|8.4|9x7|155|Vir
M50||07 03.2|-08 20|OC|5.9|16||Mon
M51|Whirlpool Galaxy|13 29.9|+47 12|Gx|8.4|11x7|163|CVn
M52||23 24.2|+61 35|OC|7.3|13||Cas
M53||13 12.9|+18 10|GC|7.6|13||Com
M54||18 55.1|-30 29|GC|7.6|12||Sgr
M55||19 40.0|-30 58|GC|6.3|19||Sgr
M56||19 16.6|+30 11|GC|8.3|9||Lyr
M57|Ring Nebula|18 53.6|+33 02|PN|8.8|1.4x1|60|Lyr
M58||12 37.7|+11 49|Gx|9.7|6x5|95|Vir
M59||12 42.0|+11 39|Gx|9.6|5x4|165|Vir
M60||12 43.7|+11 33|Gx|8.8|7x6|105|Vir
M61||12 21.9|+04 28|Gx|9.7|6x6||Vir
M62||17 01.2|-30 07|GC|6.5|15||Oph
M63|Sunflower Galaxy|13 15.8|+42 02|Gx|8.6|13x7|105|CVn
M64|Black Eye Galaxy|12 56.7|+21 41|Gx|8.5|10x5|115|Com
M65||11 18.9|+13 05|Gx|9.3|10x3|174|Leo
M66||11 20.2|+12 59|Gx|8.9|9x4|173|Leo
M67||08 51.3|+11 49|OC|6.1|30||Cnc
M68||12 39.5|-26 45|GC|7.8|11||Hya
M69||18 31.4|-32 21|GC|7.6|10||Sgr
M70||18 43.2|-32 18|GC|7.9|8||Sgr
M71||19 53.8|+18 47|GC|8.2|7||Sge
M72||20 53.5|-12 32|GC|9.3|7||Aqr
M73||20 59.0|-12 38|Ast|9.0|3||Aqr
M74|Phantom Galaxy|01 36.7|+15 47|Gx|9.4|10x9||Psc
M75||20 06.1|-21 55|GC|8.5|7||Sgr
M76|Little Dumbbell|01 42.4|+51 34|PN|10.1|3x1.5|40|Per
M77|Cetus A|02 42.7|-00 01|Gx|8.9|7x6|70|Cet
M78||05 46.7|+00 03|RN|8.3|8x6||Ori
M79||05 24.5|-24 33|GC|7.7|10||Lep
M80||16 17.0|-22 59|GC|7.3|10||Sco
M81|Bode's Galaxy|09 55.6|+69 04|Gx|6.9|27x14|157|UMa
M82|Cigar Galaxy|09 55.8|+69 41|Gx|8.4|11x5|65|UMa
M83|Southern Pinwheel|13 37.0|-29 52|Gx|7.5|13x12||Hya
M84||12 25.1|+12 53|Gx|9.1|6.5x5.6|135|Vir
M85||12 25.4|+18 11|Gx|9.1|7x5|20|Com
M86||12 26.2|+12 57|Gx|8.9|9x6|130|Vir
M87|Virgo A|12 30.8|+12 23|Gx|8.6|8x7||Vir
M88||12 32.0|+14 25|Gx|9.6|7x4|140|Com
M89||12 35.7|+12 33|Gx|9.8|5x5||Vir
M90||12 36.8|+13 10|Gx|9.5|10x4|23|Vir
M91||12 35.4|+14 30|Gx|10.2|5x4|110|Com
M92||17 17.1|+43 08|GC|6.4|14||Her
M93||07 44.6|-23 52|OC|6.2|22||Pup
M94|Cat's Eye Galaxy|12 50.9|+41 07|Gx|8.2|11x9|105|CVn
M95||10 44.0|+11 42|Gx|9.7|7x5|50|Leo
M96||10 46.8|+11 49|Gx|9.2|7x5|170|Leo
M97|Owl Nebula|11 14.8|+55 01|PN|9.9|3.4||UMa
M98||12 13.8|+14 54|Gx|10.1|10x3|153|Com
M99||12 18.8|+14 25|Gx|9.9|5x5||Com
M100||12 22.9|+15 49|Gx|9.3|7x6||Com
M101|Pinwheel Galaxy|14 03.2|+54 21|Gx|7.9|29x27||UMa
M102|Spindle Galaxy|15 06.5|+55 46|Gx|9.9|5x2|128|Dra
M103||01 33.2|+60 42|OC|7.4|6||Cas
M104|Sombrero Galaxy|12 40.0|-11 37|Gx|8.0|9x4|89|Vir
M105||10 47.8|+12 35|Gx|9.3|5x5||Leo
M106||12 19.0|+47 18|Gx|8.4|19x8|150|CVn
M107||16 32.5|-13 03|GC|7.9|13||Oph
M108|Surfboard Galaxy|11 11.5|+55 40|Gx|10.0|8x2|79|UMa
M109||11 57.6|+53 23|Gx|9.8|8x5|23|UMa
M110||00 40.4|+41 41|Gx|8.5|22x11|170|And
NGC 869|h Persei|02 19.0|+57 09|OC|5.3|30||Per
NGC 884|Chi Persei|02 22.4|+57 07|OC|6.1|30||Per
NGC 7000|North America Nebula|20 59.3|+44 31|EN|4.0|120x100||Cyg
IC 5070|Pelican Nebula|20 50.8|+44 21|EN|8.0|60x50||Cyg
NGC 6960|Western Veil Nebula|20 45.7|+30 43|SNR|7.0|70x6|10|Cyg
NGC 6992|Eastern Veil Nebula|20 56.4|+31 43|SNR|7.0|60x8|165|Cyg
NGC 253|Sculptor Galaxy|00 47.6|-25 17|Gx|7.1|27x7|52|Scl
NGC 5139|Omega Centauri|13 26.8|-47 29|GC|3.7|36||Cen
NGC 104|47 Tucanae|00 24.1|-72 05|GC|4.1|31||Tuc
NGC 7293|Helix Nebula|22 29.6|-20 50|PN|7.6|16x12||Aqr
NGC 2237|Rosette Nebula|06 32.3|+05 03|EN|9.0|80x60||Mon
IC 434|Horsehead Nebula|05 40.9|-02 28|EN|7.3|60x10||Ori
NGC 2024|Flame Nebula|05 41.9|-01 51|EN|7.2|30x30||Ori
IC 1805|Heart Nebula|02 33.4|+61 27|EN|6.5|60x60||Cas
IC 1848|Soul Nebula|02 51.2|+60 26|EN|6.5|60x30||Cas
NGC 7635|Bubble Nebula|23 20.8|+61 12|EN|10.0|15x8||Cas
LMC|Large Magellanic Cloud|05 23.6|-69 45|Gx|0.9|650x550||Dor
SMC|Small Magellanic Cloud|00 52.7|-72 50|Gx|2.7|320x185||Tuc
NGC 3372|Carina Nebula|10 45.1|-59 52|EN|1.0|120x120||Car
NGC 6543|Cat's Eye Nebula|17 58.6|+66 38|PN|8.1|0.4x0.3||Dra
NGC 7662|Blue Snowball|23 25.9|+42 32|PN|8.3|0.5||And
NGC 891||02 22.6|+42 21|Gx|9.9|13x3|22|And
NGC 4565|Needle Galaxy|12 36.3|+25 59|Gx|9.6|16x2|136|Com
NGC 2392|Eskimo Nebula|07 29.2|+20 55|PN|9.1|0.8||Gem
NGC 6888|Crescent Nebula|20 12.0|+38 21|EN|7.4|18x12||Cyg
IC 5146|Cocoon Nebula|21 53.5|+47 16|EN|7.2|12x12||Cyg
NGC 281|Pacman Nebula|00 52.8|+56 37|EN|7.4|35x30||Cas
NGC 2264|Cone Nebula & Christmas Tree|06 41.1|+09 53|CN|3.9|20x20||Mon
NGC 1499|California Nebula|04 03.3|+36 25|EN|6.0|145x40|130|Per
IC 2118|Witch Head Nebula|05 04.9|-07 13|RN|13.0|180x60||Eri
NGC 3628|Hamburger Galaxy|11 20.3|+13 35|Gx|9.5|15x4|104|Leo
NGC 4631|Whale Galaxy|12 42.1|+32 32|Gx|9.2|15x3|86|CVn
NGC 6826|Blinking Planetary|19 44.8|+50 31|PN|8.8|0.5||Cyg
NGC 7009|Saturn Nebula|21 04.2|-11 22|PN|8.0|0.5x0.3||Aqr
NGC 7380|Wizard Nebula|22 47.0|+58 08|CN|7.2|25||Cep
IC 1396|Elephant's Trunk Nebula|21 39.1|+57 30|CN|3.5|170x140||Cep
NGC 6946|Fireworks Galaxy|20 34.9|+60 09|Gx|8.8|11x10||Cep
NGC 2403||07 36.9|+65 36|Gx|8.4|22x12|127|Cam
NGC 5128|Centaurus A|13 25.5|-43 01|Gx|6.8|26x20|35|Cen
NGC 4038|Antennae Galaxies|12 01.9|-18 52|Gx|10.3|5x3||Crv
NGC 2070|Tarantula Nebula|05 38.7|-69 06|EN|8.0|40x25||Dor
NGC 6302|Bug Nebula|17 13.7|-37 06|PN|9.6|1.5x0.5||Sco
NGC 6611|Eagle cluster|18 18.8|-13 48|OC|6.0|7||Ser
Sh2-155|Cave Nebula|22 56.8|+62 37|EN|7.7|50x30||Cep
IC 405|Flaming Star Nebula|05 16.2|+34 16|EN|6.0|37x19||Aur
IC 410|Tadpoles Nebula|05 22.6|+33 31|CN|7.5|40x30||Aur
NGC 1977|Running Man Nebula|05 35.5|-04 52|RN|7.0|20x10||Ori
B 33|Horsehead (dark)|05 40.9|-02 28|DN||6x4||Ori
NGC 6820|Sh2-86|19 42.5|+23 05|EN|7.1|40x30||Vul
NGC 457|Owl Cluster|01 19.1|+58 20|OC|6.4|13||Cas
NGC 663||01 46.0|+61 15|OC|7.1|16||Cas
NGC 7331||22 37.1|+34 25|Gx|9.5|11x4|171|Peg
NGC 7317|Stephan's Quintet|22 36.1|+33 57|GxG|13.0|4x3||Peg
NGC 2359|Thor's Helmet|07 18.5|-13 13|EN|11.5|9x6||CMa
NGC 6334|Cat's Paw Nebula|17 20.5|-35 43|EN|10.0|35x20||Sco
NGC 2244|Rosette Cluster|06 32.4|+04 52|OC|4.8|24||Mon
Mel 111|Coma Star Cluster|12 25.1|+26 07|OC|1.8|275||Com
Cr 399|Coathanger|19 25.4|+20 11|Ast|3.6|60||Vul
NGC 2632|Praesepe|08 40.1|+19 59|OC|3.7|95||Cnc
";

        // Abbr|Name|Genitive|label RA (h)|label Dec (deg)
        public const string Constellations = @"
And|Andromeda|Andromedae|0.8|38
Ant|Antlia|Antliae|10.3|-32
Aps|Apus|Apodis|16.1|-75
Aqr|Aquarius|Aquarii|22.3|-11
Aql|Aquila|Aquilae|19.7|3
Ara|Ara|Arae|17.4|-56
Ari|Aries|Arietis|2.6|21
Aur|Auriga|Aurigae|6.0|42
Boo|Boötes|Boötis|14.7|31
Cae|Caelum|Caeli|4.7|-38
Cam|Camelopardalis|Camelopardalis|6.0|70
Cnc|Cancer|Cancri|8.6|20
CVn|Canes Venatici|Canum Venaticorum|13.1|40
CMa|Canis Major|Canis Majoris|6.8|-22
CMi|Canis Minor|Canis Minoris|7.6|6
Cap|Capricornus|Capricorni|21.0|-18
Car|Carina|Carinae|8.7|-63
Cas|Cassiopeia|Cassiopeiae|1.3|62
Cen|Centaurus|Centauri|13.1|-47
Cep|Cepheus|Cephei|22.0|71
Cet|Cetus|Ceti|1.7|-7
Cha|Chamaeleon|Chamaeleontis|10.7|-79
Cir|Circinus|Circini|14.6|-63
Col|Columba|Columbae|5.9|-35
Com|Coma Berenices|Comae Berenices|12.8|23
CrA|Corona Australis|Coronae Australis|18.6|-41
CrB|Corona Borealis|Coronae Borealis|15.8|33
Crv|Corvus|Corvi|12.4|-18
Crt|Crater|Crateris|11.4|-16
Cru|Crux|Crucis|12.4|-60
Cyg|Cygnus|Cygni|20.6|44
Del|Delphinus|Delphini|20.7|12
Dor|Dorado|Doradus|5.2|-60
Dra|Draco|Draconis|17.0|66
Equ|Equuleus|Equulei|21.2|8
Eri|Eridanus|Eridani|3.3|-29
For|Fornax|Fornacis|2.8|-32
Gem|Gemini|Geminorum|7.1|23
Gru|Grus|Gruis|22.5|-47
Her|Hercules|Herculis|17.4|27
Hor|Horologium|Horologii|3.3|-53
Hya|Hydra|Hydrae|11.6|-14
Hyi|Hydrus|Hydri|2.3|-70
Ind|Indus|Indi|21.9|-60
Lac|Lacerta|Lacertae|22.5|46
Leo|Leo|Leonis|10.7|14
LMi|Leo Minor|Leonis Minoris|10.2|33
Lep|Lepus|Leporis|5.6|-19
Lib|Libra|Librae|15.2|-15
Lup|Lupus|Lupi|15.2|-43
Lyn|Lynx|Lyncis|7.9|48
Lyr|Lyra|Lyrae|18.9|37
Men|Mensa|Mensae|5.4|-77
Mic|Microscopium|Microscopii|21.0|-36
Mon|Monoceros|Monocerotis|7.1|0
Mus|Musca|Muscae|12.6|-70
Nor|Norma|Normae|16.1|-51
Oct|Octans|Octantis|23.0|-82
Oph|Ophiuchus|Ophiuchi|17.4|-8
Ori|Orion|Orionis|5.6|6
Pav|Pavo|Pavonis|19.6|-66
Peg|Pegasus|Pegasi|22.7|20
Per|Perseus|Persei|3.2|45
Phe|Phoenix|Phoenicis|0.9|-48
Pic|Pictor|Pictoris|5.7|-53
Psc|Pisces|Piscium|0.5|13
PsA|Piscis Austrinus|Piscis Austrini|22.3|-31
Pup|Puppis|Puppis|7.3|-32
Pyx|Pyxis|Pyxidis|8.9|-27
Ret|Reticulum|Reticuli|3.9|-60
Sge|Sagitta|Sagittae|19.7|19
Sgr|Sagittarius|Sagittarii|19.1|-28
Sco|Scorpius|Scorpii|16.9|-30
Scl|Sculptor|Sculptoris|0.4|-32
Sct|Scutum|Scuti|18.7|-10
Ser|Serpens|Serpentis|16.9|5
Sex|Sextans|Sextantis|10.3|-3
Tau|Taurus|Tauri|4.7|15
Tel|Telescopium|Telescopii|19.3|-51
Tri|Triangulum|Trianguli|2.2|32
TrA|Triangulum Australe|Trianguli Australis|16.1|-65
Tuc|Tucana|Tucanae|23.8|-66
UMa|Ursa Major|Ursae Majoris|11.3|51
UMi|Ursa Minor|Ursae Minoris|15.0|78
Vel|Vela|Velorum|9.6|-47
Vir|Virgo|Virginis|13.4|-4
Vol|Volans|Volantis|7.8|-69
Vul|Vulpecula|Vulpeculae|20.2|24
";

        /// <summary>Stick figures for major constellations using the built-in bright stars (by name pairs).</summary>
        public const string BuiltInLines = @"
Ori|Betelgeuse-Bellatrix|Bellatrix-Mintaka|Mintaka-Alnilam|Alnilam-Alnitak|Alnitak-Saiph|Saiph-Rigel|Rigel-Mintaka|Betelgeuse-Alnitak
UMa|Dubhe-Merak|Merak-Phecda|Phecda-Megrez|Megrez-Dubhe|Megrez-Alioth|Alioth-Mizar|Mizar-Alkaid
Cas|Caph-Schedar|Schedar-Navi|Navi-Ruchbah
Cyg|Deneb-Sadr|Sadr-Albireo|Sadr-Aljanah
Leo|Regulus-Algieba|Algieba-Zosma|Zosma-Denebola
Sco|Acrab-Dschubba|Dschubba-Antares|Antares-Larawag|Larawag-Sargas|Sargas-Shaula
Cru|Acrux-Gacrux|Mimosa-Gacrux
Gem|Castor-Pollux|Pollux-Alhena
Peg|Markab-Scheat|Scheat-Alpheratz|Alpheratz-Algenib|Algenib-Markab|Markab-Enif
And|Alpheratz-Mirach|Mirach-Almach
Aql|Tarazed-Altair
Tau|Aldebaran-Elnath
Aur|Capella-Menkalinan
CMa|Sirius-Mirzam|Sirius-Wezen|Wezen-Adhara|Wezen-Aludra
UMi|Polaris-Kochab
Sgr|Kaus Australis-Nunki|Kaus Australis-Alnasl
Boo|Arcturus-Izar
Cen|Rigil Kentaurus-Hadar
";
    }
}
