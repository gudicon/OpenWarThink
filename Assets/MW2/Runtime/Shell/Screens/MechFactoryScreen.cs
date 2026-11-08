using System.Collections.Generic;

namespace MW2.Shell
{
    /// <summary>
    /// Mech Factory 0xc01d (mechfact.cpp: open FUN_10038251 0x10038251, screen function FUN_10038eb7 0x10038eb7; the trade
    /// is <see cref="MechFactory"/>). Background 17 (DAT_100a7698), colour 1 → 0x16 / 0x11 / 0x9d. Without mechs on
    /// either side: "There are no transactions to be made!" and back. The unit's mechs are listed at (461, 94), the
    /// market's at (257, 94), a line each 100 wide (FUN_10038a5c, FUN_10038c3d); the unit's side adds "Current Load" /
    /// "Remaining Space" (of 12) at y 339 / 357. The selected mech: its name centred on 126 at y 93 in font 6, its
    /// "a&lt;code&gt;" picture at (44, 207) and the price and balance (FUN_10037efa). Spots: 0 exit to the drop ship
    /// 0xc032, 1 sell ("mfbs"), 2 buy ("mfbb"), 3 the figures instead of the picture ("mfbc" over "mfbd"), 4 / 5 next /
    /// previous mech ("mfbn" / "mfbp"); a double click sells or buys. Random chatter (FUN_1003a088: records 100, 101,
    /// 119, 119 by rand() % 16 / 4, volume 15) plays while the option DAT_100a42f0 is on.
    /// </summary>
    public static class MechFactoryScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(498, 432, 601, 473, 0, 0, ""),
            new ShellRegions.Entry(488, 381, 583, 417, 0, 0, ""),
            new ShellRegions.Entry(284, 381, 380, 415, 0, 0, ""),
            new ShellRegions.Entry(45, 148, 209, 185, 0, 0, ""),
            new ShellRegions.Entry(130, 422, 225, 462, 0, 0, ""),
            new ShellRegions.Entry(34, 422, 129, 462, 0, 0, "")
        };
        static readonly int[] Chatter = { 100, 101, 119, 119 };     // DAT_100a76a0
        const int Below = unchecked((int)0x80000011), Same = unchecked((int)0x80000000);

        static Mw2Shell _s;
        static MechFactory _f;
        static ShellRegions _regions;
        static ShellSound _click, _select, _ambient, _exit, _chat;     // DAT_100cdbe4, be8, c2c, c30, bf4
        static ShellAnim _sell, _buy, _exitButton, _prev, _next, _frame, _figuresFrame, _picture;
        static ShellItemList _own, _market, _figures, _funds;
        static ShellItemList.Entry _price, _balance;
        static ShellText _name;                                     // DAT_100cdbe0
        static List<MechRecord> _list;                              // DAT_100cdbd4
        static MechRecord _mech;                                    // DAT_100cdbd8
        static int _index;                                          // DAT_100cdb6c
        static bool _showFigures, _buttonsDown, _closed;            // DAT_100cdb50, DAT_100cdc18
        static byte[] _normal, _selected;
        static List<string> _figureLines;

        public static void Open(Mw2Shell s)
        {
            _s = s;
            var c = s.Career;
            _normal = ShellSurface.MakeRemap(0x16);
            _selected = ShellSurface.MakeRemap(0x11);
            _list = null; _mech = null; _index = 0; _name = null; _picture = null; _chat = null;
            _showFigures = _buttonsDown = _closed = false;
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(0x11)));
            if (c.UnitMechs.Count == 0 && c.MarketMechs.Count == 0)
            {
                s.Dialog("There are no transactions to be made!#Ok", null);
                s.Post(c.Msg23C3, 0xc01d);
                return;
            }
            _f = new MechFactory(c, s.Data.Tables);
            s.Anims.Clear();
            _sell = Button("mfbs", 0x1ef, 0x182);
            _buy = Button("mfbb", 0x124, 0x182);
            _exitButton = Button("mfbe", 0x1fe, 0x1b8);
            _prev = Button("mfbp", 0x2f, 0x1ac);
            _next = Button("mfbn", 0x88, 0x1ae);
            _frame = Button("mfbd", 0x32, 0x87);
            _figuresFrame = new ShellAnim(s.Anims, "mfbc", 0x31, 0x85, 0x21, 0);
            _click = Sound(0x35, 0x14);
            _select = Sound(0x34, 0x14);
            _ambient = Sound(0x3b, 0x14);
            _ambient.LoopForever();
            _ambient.Play();
            _exit = Sound(0x39, 0x37);
            _own = List(0x1cd, c.UnitMechs, 0);
            _market = List(0x101, c.MarketMechs, 1);
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            _funds = ShellFunds.Build(s, 23, 36, out _price, out _balance);     // DAT_100a76b0
            _funds.Init();
            _own.Init();
            _market.Init();
            _figures = Figures();
            // FUN_10038251: the market's first mech when it has any, else the unit's first. (The market case looks the
            // mech up with the unit list's first index, 0.)
            if (c.MarketMechs.Count > 0) Select(_market, c.MarketMechs, 0);
            else if (c.UnitMechs.Count > 0) Select(_own, c.UnitMechs, 0);
            s.Register(Tick);
        }

        static void Select(ShellItemList list, List<MechRecord> mechs, int index)
        {
            list.Entries[index].State = 1;
            list.Refresh();
            _list = mechs;
            _mech = mechs[index];
            ShowInfo();
            ShowPicture();
        }

        static ShellSound Sound(int record, int volume)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            snd.SetVolume(volume);
            return snd;
        }

        static ShellAnim Button(string name, int x, int y)
        {
            var a = new ShellAnim(_s.Anims, name, x, y, 0x21, 0);
            a.Show();
            return a;
        }

        static ShellText Draw(ShellFont font, string text, int x, int y, byte[] remap)
        {
            var t = new ShellText(_s.Surface, font, text, x, y, remap);
            t.Show();
            return t;
        }

        /// <summary>FUN_10038a5c 0x10038a5c.</summary>
        static ShellItemList List(int x, List<MechRecord> mechs, int side)
        {
            var list = new ShellItemList();
            int h = _s.Font11.Height;
            for (int i = 0; i < mechs.Count; i++)
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = x, Y = 0x5e + i * h, W = 100, H = h, Remap = _normal, Data = i, Data2 = side,
                    Draw = e => Draw(_s.Font11, _f.ListName(mechs[e.Data]), e.X, e.Y, e.State == 1 ? _selected : e.Remap),
                    Click = e => { _select.Play(); e.State = 2; }       // FUN_10038d95
                });
            if (side == 0)
            {
                list.Entries.Add(new ShellItemList.Entry { X = x, Y = 0x153, H = 0, Draw = e => Draw(_s.Font11, $"Current Load                 : {mechs.Count}", e.X, e.Y, _normal) });
                list.Entries.Add(new ShellItemList.Entry { X = x, Y = 0x165, H = 0, Draw = e => Draw(_s.Font11, $"Remaining Space           : {MechFactory.Capacity - mechs.Count}", e.X, e.Y, _normal) });
            }
            return list;
        }

        /// <summary>DAT_100a7838: 23 entries from (43, 206), one below the other (the seventh, FUN_10037a83, on the same line,
        /// only clears the marks).</summary>
        static ShellItemList Figures()
        {
            var list = new ShellItemList();
            for (int k = 0; k < 23; k++)
            {
                // A copy per entry: the draw lambdas run later, and the shared loop variable would be 23 for all of them
                // (entry 0 then never filled _figureLines and the others threw NullReferenceException).
                int i = k, line = i < 6 ? i : i - 1;
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = 0x2b, Y = i == 0 ? 0xce : i == 6 ? Same : Below, Remap = _normal,
                    Draw = e =>
                    {
                        if (_mech == null || i == 6) return null;
                        if (i == 0) _figureLines = _f.Figures(_mech);
                        return line < _figureLines.Count ? Draw(_s.Font11, _figureLines[line], e.X, e.Y, e.Remap) : null;
                    }
                });
            }
            return list;
        }

        /// <summary>FUN_10038daf 0x10038daf: "a&lt;code&gt;" at (44, 207), shown unless the figures are.</summary>
        static void ShowPicture()
        {
            if (_picture != null) { _picture.Hide(); _picture.Destroy(); }
            _picture = new ShellAnim(_s.Anims, "a" + _mech.Code, 0x2c, 0xcf, 0x28, 0);
            if (!_showFigures) _picture.Show();
        }

        /// <summary>FUN_10037efa 0x10037efa: the name (FUN_1004a860 to 164 in font 6) centred on 126 at y 93, the price (0.6 of
        /// the value for the unit's mech, all of it for the market's) and the balance after.</summary>
        static void ShowInfo()
        {
            _name?.Hide();
            _name = null;
            if (_mech == null) return;
            string text = _mech.Str(0x100, 0x3a);
            text = text.Length == 0 ? _f.TableName(_mech) : SkipFirstWord(text, _s.Font6, 0xa4);
            _name = Draw(_s.Font6, text, 0x7e - _s.Font6.Width(text) / 2, 0x5d, _selected);
            bool market = _s.Career.MarketMechs.Contains(_mech);
            int v = market ? _f.BuyPrice(_mech) : _f.SellPrice(_mech);
            _price.Data = v;
            _balance.Data = market ? _s.Career.Funds - v : _s.Career.Funds + v;
            ShellFunds.ShowPrice(_funds, true);     // FUN_100380fd
        }

        /// <summary>FUN_1004a860 0x1004a860.</summary>
        static string SkipFirstWord(string text, ShellFont font, int width)
        {
            if (font.Width(text) <= width) return text;
            int k = 0;
            while (k < text.Length && text[k] != ' ') k++;
            while (k < text.Length && text[k] == ' ') k++;
            return k < text.Length ? text.Substring(k) : text;
        }

        static void RefreshFigures()
        {
            if (!_showFigures) return;
            _figures.Free();
            _figures.Init();
        }

        /// <summary>After a sale or a purchase: nothing selected, the price lines hidden (FUN_1003a1bc), both lists anew.</summary>
        static void AfterTrade()
        {
            var c = _s.Career;
            _mech = null;
            _name?.Hide();
            _name = null;
            ShellFunds.ShowPrice(_funds, false);
            _figures.Free();
            _picture?.Hide();
            _own.Free();
            _own = List(0x1cd, c.UnitMechs, 0);
            _own.Init();
            _market.Free();
            _market = List(0x101, c.MarketMechs, 1);
            _market.Init();
            _list = null;
        }

        static void Press(ShellAnim button)
        {
            button.SetFrame(1);
            _s.Anims.Step();
            _click.Play();
            _buttonsDown = true;
        }

        static void SellSelected()
        {
            var c = _s.Career;
            if (_list == null || _list == c.MarketMechs || c.UnitMechs.Count == 0 || _mech == null) return;
            Press(_sell);
            var mech = _mech;
            _s.Dialog("Sell this Mech?#Yes|No", a =>
            {
                if (a != 0) return;
                _f.SellMech(mech);
                AfterTrade();
                if (!ShellEconomy.CanAffordAnyMech(c, _s.Data.Tables)) Leave(0xc037);
            });
        }

        static void BuySelected()
        {
            var c = _s.Career;
            if (_list == null || _list == c.UnitMechs || c.MarketMechs.Count == 0 || _mech == null) return;
            Press(_buy);
            if (c.UnitMechs.Count == MechFactory.Capacity) _s.Dialog("Insufficent space on dropship!|#Ok", null);
            else if (_f.CanAfford(_mech))
            {
                var mech = _mech;
                _s.Dialog("Buy this Mech?#Yes|No", a =>
                {
                    if (a != 0) return;
                    _f.BuyMech(mech);
                    AfterTrade();
                });
            }
            else _s.Dialog("Insufficient Funds!#Ok", null);
        }

        static void Step(int step)
        {
            if (_list == null) return;
            Press(step > 0 ? _next : _prev);
            _index += step;
            if (_index >= _list.Count) _index = 0;
            if (_index < 0) _index = _list.Count - 1;
            _mech = _list[_index];
            ShowPicture();
            ShowInfo();
            // FUN_1004b6ff: every entry whose +0x24 is the index (the load lines' too, for index 0).
            var list = _list == _s.Career.UnitMechs ? _own : _market;
            list.ClearStates();
            foreach (var e in list.Entries) if (e.Data == _index && e.Click != null) e.State = 1;
            list.Refresh();
            RefreshFigures();
        }

        /// <summary>FUN_1003a088 0x1003a088.</summary>
        static void Chat()
        {
            if (_chat != null)
            {
                if (_chat.Playing) return;
                _chat.Release();
                _chat = null;
            }
            int record = Chatter[_s.Rand() % 0x10 / 4];
            if (record == 1) return;
            _chat = Sound(record, 0xf);
            _chat.Play();
        }

        static void Tick(uint ev)
        {
            var s = _s;
            var c = s.Career;
            if (ev == 0xc004)
            {
                Chat();
                if (_buttonsDown)
                {
                    _buttonsDown = false;
                    foreach (var b in new[] { _sell, _buy, _exitButton, _prev, _next, _frame }) b.SetFrame(0);
                }
                s.Anims.Step();
                bool down = s.MouseClicked;
                if (!down) return;
                var e = _own.Hit(s.MouseX, s.MouseY);
                if (e != null)
                {
                    _list = c.UnitMechs;
                    e.Click(e);
                    if (s.MouseDouble) { SellSelected(); return; }
                    Pick(e, _own, _market);
                }
                else if ((e = _market.Hit(s.MouseX, s.MouseY)) != null)
                {
                    _list = c.MarketMechs;
                    e.Click(e);
                    Pick(e, _market, _own);
                    if (s.MouseDouble) { BuySelected(); return; }
                }
                switch (_regions.Hit(s.MouseX, s.MouseY))
                {
                    case 0:
                        _exitButton.SetFrame(1);
                        s.Anims.Step();
                        s.BlockUntilEnd(_exit);
                        ev = 0xc032;
                        break;
                    case 1: SellSelected(); break;
                    case 2: BuySelected(); break;
                    case 3:
                        if (_mech == null) break;
                        _click.Play();
                        _showFigures = !_showFigures;
                        if (_showFigures)
                        {
                            _figuresFrame.Show();
                            _frame.Hide();
                            _picture?.Hide();
                            _figures.Init();
                        }
                        else
                        {
                            _figuresFrame.Hide();
                            _frame.Show();
                            _picture?.Show();
                            _figures.Free();
                        }
                        break;
                    case 4: Step(1); break;
                    case 5: Step(-1); break;
                }
            }
            if (ev != 0xc004) Leave(ev);
        }

        static void Pick(ShellItemList.Entry e, ShellItemList list, ShellItemList other)
        {
            _mech = _list[e.Data];
            ShowPicture();
            RefreshFigures();
            _index = e.Data;
            list.Refresh();
            other.ClearStates();
            other.Refresh();
            ShowInfo();
        }

        static void Leave(uint ev)
        {
            if (_closed) return;
            _closed = true;
            var s = _s;
            _regions.Dispose(); _regions = null;
            s.Anims.Clear();
            _funds.Free(); _figures.Free(); _own.Free(); _market.Free();
            _name?.Hide();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            _ambient.Release(); _exit.Release(); _click.Release(); _chat?.Release(); _select.Release();
            s.Post(ev, 0xc01d);
            s.Unregister();
        }
    }
}
