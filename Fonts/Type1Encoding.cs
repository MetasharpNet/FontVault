using System.Globalization;

namespace FontVault.Fonts;

/// <summary>Adobe StandardEncoding: code (0–255) → glyph name (null where unassigned).</summary>
internal static class Type1Encoding
{
    public static readonly string?[] Standard = Build();

    private static string?[] Build()
    {
        var t = new string?[256];
        void S(int c, string n) => t[c] = n;
        S(32, "space"); S(33, "exclam"); S(34, "quotedbl"); S(35, "numbersign"); S(36, "dollar");
        S(37, "percent"); S(38, "ampersand"); S(39, "quoteright"); S(40, "parenleft"); S(41, "parenright");
        S(42, "asterisk"); S(43, "plus"); S(44, "comma"); S(45, "hyphen"); S(46, "period"); S(47, "slash");
        string[] digits = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
        for (int i = 0; i < 10; i++) S(48 + i, digits[i]);
        S(58, "colon"); S(59, "semicolon"); S(60, "less"); S(61, "equal"); S(62, "greater"); S(63, "question"); S(64, "at");
        for (int i = 0; i < 26; i++) S(65 + i, ((char)('A' + i)).ToString());
        S(91, "bracketleft"); S(92, "backslash"); S(93, "bracketright"); S(94, "asciicircum"); S(95, "underscore"); S(96, "quoteleft");
        for (int i = 0; i < 26; i++) S(97 + i, ((char)('a' + i)).ToString());
        S(123, "braceleft"); S(124, "bar"); S(125, "braceright"); S(126, "asciitilde");
        S(161, "exclamdown"); S(162, "cent"); S(163, "sterling"); S(164, "fraction"); S(165, "yen");
        S(166, "florin"); S(167, "section"); S(168, "currency"); S(169, "quotesingle"); S(170, "quotedblleft");
        S(171, "guillemotleft"); S(172, "guilsinglleft"); S(173, "guilsinglright"); S(174, "fi"); S(175, "fl");
        S(177, "endash"); S(178, "dagger"); S(179, "daggerdbl"); S(180, "periodcentered"); S(182, "paragraph");
        S(183, "bullet"); S(184, "quotesinglbase"); S(185, "quotedblbase"); S(186, "quotedblright"); S(187, "guillemotright");
        S(188, "ellipsis"); S(189, "perthousand"); S(191, "questiondown"); S(193, "grave"); S(194, "acute");
        S(195, "circumflex"); S(196, "tilde"); S(197, "macron"); S(198, "breve"); S(199, "dotaccent");
        S(200, "dieresis"); S(202, "ring"); S(203, "cedilla"); S(205, "hungarumlaut"); S(206, "ogonek");
        S(207, "caron"); S(208, "emdash"); S(225, "AE"); S(227, "ordfeminine"); S(232, "Lslash");
        S(233, "Oslash"); S(234, "OE"); S(235, "ordmasculine"); S(241, "ae"); S(245, "dotlessi");
        S(248, "lslash"); S(249, "oslash"); S(250, "oe"); S(251, "germandbls");
        return t;
    }
}

/// <summary>Glyph name → Unicode (AGL subset + uniXXXX/uXXXXXX). Returns -1 if unknown.</summary>
internal static class GlyphList
{
    public static int ToUnicode(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        int dot = name.IndexOf('.');
        string b = dot > 0 ? name[..dot] : name;
        if (Map.TryGetValue(b, out int cp)) return cp;
        if (b.Length == 7 && b.StartsWith("uni", StringComparison.Ordinal) &&
            int.TryParse(b.AsSpan(3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u)) return u;
        if (b.Length is >= 5 and <= 7 && b[0] == 'u' &&
            int.TryParse(b.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u2)) return u2;
        return -1;
    }

    private static readonly Dictionary<string, int> Map = Build();

    private static Dictionary<string, int> Build()
    {
        var m = new Dictionary<string, int>(StringComparer.Ordinal);
        // ASCII
        void A(string n, int c) => m[n] = c;
        A("space", 0x20); A("exclam", 0x21); A("quotedbl", 0x22); A("numbersign", 0x23); A("dollar", 0x24);
        A("percent", 0x25); A("ampersand", 0x26); A("quotesingle", 0x27); A("quoteright", 0x2019); A("quoteleft", 0x2018);
        A("parenleft", 0x28); A("parenright", 0x29); A("asterisk", 0x2A); A("plus", 0x2B); A("comma", 0x2C);
        A("hyphen", 0x2D); A("period", 0x2E); A("slash", 0x2F); A("colon", 0x3A); A("semicolon", 0x3B);
        A("less", 0x3C); A("equal", 0x3D); A("greater", 0x3E); A("question", 0x3F); A("at", 0x40);
        A("bracketleft", 0x5B); A("backslash", 0x5C); A("bracketright", 0x5D); A("asciicircum", 0x5E); A("underscore", 0x5F);
        A("grave", 0x60); A("braceleft", 0x7B); A("bar", 0x7C); A("braceright", 0x7D); A("asciitilde", 0x7E);
        string[] digits = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
        for (int i = 0; i < 10; i++) A(digits[i], 0x30 + i);
        for (int i = 0; i < 26; i++) { A(((char)('A' + i)).ToString(), 0x41 + i); A(((char)('a' + i)).ToString(), 0x61 + i); }
        // Latin-1 punctuation / symbols
        A("exclamdown", 0xA1); A("cent", 0xA2); A("sterling", 0xA3); A("fraction", 0x2044); A("yen", 0xA5);
        A("florin", 0x192); A("section", 0xA7); A("currency", 0xA4); A("quotedblleft", 0x201C); A("quotedblright", 0x201D);
        A("quotedblbase", 0x201E); A("quotesinglbase", 0x201A); A("guillemotleft", 0xAB); A("guillemotright", 0xBB);
        A("guilsinglleft", 0x2039); A("guilsinglright", 0x203A); A("fi", 0xFB01); A("fl", 0xFB02);
        A("endash", 0x2013); A("emdash", 0x2014); A("dagger", 0x2020); A("daggerdbl", 0x2021); A("periodcentered", 0xB7);
        A("paragraph", 0xB6); A("bullet", 0x2022); A("ellipsis", 0x2026); A("perthousand", 0x2030); A("questiondown", 0xBF);
        A("acute", 0xB4); A("circumflex", 0x2C6); A("tilde", 0x2DC); A("macron", 0xAF); A("breve", 0x2D8);
        A("dotaccent", 0x2D9); A("dieresis", 0xA8); A("ring", 0x2DA); A("cedilla", 0xB8); A("hungarumlaut", 0x2DD);
        A("ogonek", 0x2DB); A("caron", 0x2C7); A("degree", 0xB0); A("plusminus", 0xB1); A("multiply", 0xD7);
        A("divide", 0xF7); A("minus", 0x2212); A("mu", 0xB5); A("brokenbar", 0xA6); A("logicalnot", 0xAC);
        A("registered", 0xAE); A("copyright", 0xA9); A("trademark", 0x2122); A("Euro", 0x20AC); A("euro", 0x20AC);
        A("onequarter", 0xBC); A("onehalf", 0xBD); A("threequarters", 0xBE); A("onesuperior", 0xB9);
        A("twosuperior", 0xB2); A("threesuperior", 0xB3); A("ordfeminine", 0xAA); A("ordmasculine", 0xBA);
        // Ligatures / special Latin
        A("AE", 0xC6); A("ae", 0xE6); A("OE", 0x152); A("oe", 0x153); A("Oslash", 0xD8); A("oslash", 0xF8);
        A("Lslash", 0x141); A("lslash", 0x142); A("germandbls", 0xDF); A("dotlessi", 0x131);
        A("Thorn", 0xDE); A("thorn", 0xFE); A("Eth", 0xD0); A("eth", 0xF0);
        // Accented Latin
        void Acc(string n, int c) => m[n] = c;
        Acc("Agrave", 0xC0); Acc("Aacute", 0xC1); Acc("Acircumflex", 0xC2); Acc("Atilde", 0xC3); Acc("Adieresis", 0xC4); Acc("Aring", 0xC5);
        Acc("Ccedilla", 0xC7); Acc("Egrave", 0xC8); Acc("Eacute", 0xC9); Acc("Ecircumflex", 0xCA); Acc("Edieresis", 0xCB);
        Acc("Igrave", 0xCC); Acc("Iacute", 0xCD); Acc("Icircumflex", 0xCE); Acc("Idieresis", 0xCF); Acc("Ntilde", 0xD1);
        Acc("Ograve", 0xD2); Acc("Oacute", 0xD3); Acc("Ocircumflex", 0xD4); Acc("Otilde", 0xD5); Acc("Odieresis", 0xD6);
        Acc("Ugrave", 0xD9); Acc("Uacute", 0xDA); Acc("Ucircumflex", 0xDB); Acc("Udieresis", 0xDC); Acc("Yacute", 0xDD);
        Acc("agrave", 0xE0); Acc("aacute", 0xE1); Acc("acircumflex", 0xE2); Acc("atilde", 0xE3); Acc("adieresis", 0xE4); Acc("aring", 0xE5);
        Acc("ccedilla", 0xE7); Acc("egrave", 0xE8); Acc("eacute", 0xE9); Acc("ecircumflex", 0xEA); Acc("edieresis", 0xEB);
        Acc("igrave", 0xEC); Acc("iacute", 0xED); Acc("icircumflex", 0xEE); Acc("idieresis", 0xEF); Acc("ntilde", 0xF1);
        Acc("ograve", 0xF2); Acc("oacute", 0xF3); Acc("ocircumflex", 0xF4); Acc("otilde", 0xF5); Acc("odieresis", 0xF6);
        Acc("ugrave", 0xF9); Acc("uacute", 0xFA); Acc("ucircumflex", 0xFB); Acc("udieresis", 0xFC);
        Acc("yacute", 0xFD); Acc("ydieresis", 0xFF);
        return m;
    }
}
