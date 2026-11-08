namespace MW2.Shell
{
    /// <summary>
    /// The funds block several trade screens share (the repair bay's DAT_100b89b8, the Arms Merchant's DAT_10096558):
    /// "Funds:" at (488, 8) and the funds right-aligned at 624 (FUN_1004a4bd / FUN_100456d3), then "Price:" and "Bal:"
    /// at x 488 with their amounts right-aligned at 624 (FUN_1004a528 / FUN_1004a5a1 / FUN_1004a64b), shown once +0x28 is
    /// set; font 7, colour 1 → 0x11, nothing in novice and instant-action careers (+0x23bb 0 / 2).
    /// </summary>
    public static class ShellFunds
    {
        public static ShellItemList Build(Mw2Shell s, int priceY, int balanceY, out ShellItemList.Entry price, out ShellItemList.Entry balance)
        {
            var c = s.Career;
            var remap = ShellSurface.MakeRemap(0x11);
            bool Hidden() => c.Mode == 0 || c.Mode == 2;
            ShellText Text(string text, int x, int y)
            {
                var t = new ShellText(s.Surface, s.Font7, text, x, y, remap);
                t.Show();
                return t;
            }
            ShellText Right(string text, int x, int y) => Text(text, x - s.Font7.Width(text), y);
            const int Same = unchecked((int)0x80000000);
            var list = new ShellItemList();
            list.Entries.Add(new ShellItemList.Entry { X = 488, Y = 8, Draw = e => Hidden() ? null : Text("Funds:", e.X, e.Y) });
            list.Entries.Add(new ShellItemList.Entry { X = 624, Y = 8, Draw = e => Hidden() ? null : Right(Debriefing.Money(c.Funds), e.X, e.Y) });
            list.Entries.Add(new ShellItemList.Entry { X = 488, Y = priceY, Draw = e => e.Data2 == 0 || Hidden() ? null : Text("Price:", e.X, e.Y) });
            price = new ShellItemList.Entry { X = 624, Y = Same, Draw = e => e.Data2 == 0 || Hidden() ? null : Right(Debriefing.Money(e.Data), e.X, e.Y) };
            list.Entries.Add(price);
            list.Entries.Add(new ShellItemList.Entry { X = 488, Y = balanceY, Draw = e => e.Data2 == 0 || Hidden() ? null : Text("Bal:", e.X, e.Y) });
            balance = new ShellItemList.Entry { X = 624, Y = Same, Draw = e => e.Data2 == 0 || Hidden() ? null : Right(Debriefing.Money(e.Data), e.X, e.Y) };
            list.Entries.Add(balance);
            return list;
        }

        /// <summary>+0x28 of the last four entries (FUN_1005ec42 / FUN_10003960 set it, FUN_10004341 clears it), then a redraw.</summary>
        public static void ShowPrice(ShellItemList list, bool show)
        {
            for (int i = 2; i < 6; i++) list.Entries[i].Data2 = show ? 1 : 0;
            list.Refresh();
        }
    }
}
