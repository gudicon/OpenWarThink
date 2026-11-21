using System;
using System.Globalization;
using System.Text;

namespace MW2.Shell
{
    /// <summary>Figures shown by the office finance terminal, FUN_100284c0
    /// 0x100284c0. Its displayed costs precede the campaign expense multiplier.</summary>
    public sealed class FinanceReport
    {
        public int Maintenance, TechnicianSalaries, Travel, BrokerFees, GuildDues;
        public int PilotSalaries, AeroTechSalaries, MonthlyCosts;
        public int Funds, InventoryValue, NetWorth;

        /// <summary>FUN_100284c0 0x100284c0: the actual textpage.cpp stream, including large lines, tabs and
        /// right-aligned amounts. DAT_100a3e6c..100a4044 supply the strings; FUN_100496d6 inserts commas.</summary>
        public static string PageText(FinanceReport r, Career career, ShellFont normal, ShellFont large)
        {
            const int right = 0x118;
            string Money(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
            string Amount(int value, ShellFont font)
            {
                string money = Money(value);
                return $"\\g{right:000}\\b{font.Width(money):000}{money}";
            }
            var b = new StringBuilder();
            b.Append(ShellFont.Tr("\\lExpenses: ")).Append($"\\g{right:000}\\b{large.Width("C-Bills"):000}" + ShellFont.Tr("C-Bills")).Append("\\n\\n");
            b.Append(ShellFont.Tr("Unit Upkeep:") + "\\n");
            b.Append(ShellFont.Tr("\\tBattleMech Maintenance:")).Append(Amount(r.Maintenance, normal)).Append("\\n");
            b.Append(ShellFont.Tr("\\tTechnician Salaries:")).Append(Amount(r.TechnicianSalaries, normal)).Append("\\n");
            b.Append(ShellFont.Tr("\\tTravel Costs:")).Append(Amount(r.Travel, normal)).Append("\\n");
            b.Append(ShellFont.Tr("\\tBroker Fees:")).Append(Amount(r.BrokerFees, normal)).Append("\\n");
            b.Append(ShellFont.Tr("\\tGuild Dues:")).Append(Amount(r.GuildDues, normal)).Append("\\n");
            if (career.UnitPilots.Count > 1) b.Append("\\n" + ShellFont.Tr("Pilot Salaries:") + "\\n");
            foreach (var pilot in career.UnitPilots)
                if (!pilot.Player) b.Append("\\t").Append(ShellFont.Tr(pilot.Name)).Append(Amount(pilot.I32(0x3c), normal)).Append("\\n");
            foreach (var tech in career.UnitTechs)
                b.Append(ShellFont.Tr("\\tAeroTech: ")).Append(ShellFont.Tr(tech.Name)).Append(Amount(tech.I32(0x24), normal)).Append("\\n");
            b.Append("\\n" + ShellFont.Tr("Total Monthly Costs: ")).Append(Amount(r.MonthlyCosts, normal)).Append("\\n\\n");
            b.Append(ShellFont.Tr("\\lAvailable Funds: ")).Append(Amount(r.Funds, large)).Append("\\n");
            b.Append(ShellFont.Tr("\\lTotal Inventory Value: ")).Append(Amount(r.InventoryValue, large)).Append("\\n\\n");
            b.Append(ShellFont.Tr("\\lTotal Net Worth: ")).Append(Amount(r.NetWorth, large)).Append("\\n\\n");
            return b.ToString();
        }

        public static FinanceReport Read(Career career, ShellTables tables)
        {
            if (career == null) throw new ArgumentNullException(nameof(career));
            if (tables == null) throw new ArgumentNullException(nameof(tables));
            int State(int offset) => BitConverter.ToInt32(career.Data, offset);
            var report = new FinanceReport
            {
                Maintenance = State(0x23a3) * career.UnitMechs.Count,
                TechnicianSalaries = State(0x23a7) * career.UnitMechs.Count + 1,
                Travel = State(0x23ab),
                BrokerFees = State(0x23af),
                GuildDues = State(0x23b3),
                Funds = career.Funds,
                InventoryValue = ShellEconomy.UnitAssets(career, tables)
            };
            foreach (var pilot in career.UnitPilots)
                if (!pilot.Player) report.PilotSalaries += pilot.I32(0x3c);
            foreach (var tech in career.UnitTechs)
                report.AeroTechSalaries += tech.I32(0x24);
            report.MonthlyCosts = report.Maintenance + report.TechnicianSalaries + report.Travel +
                report.BrokerFees + report.GuildDues + report.PilotSalaries + report.AeroTechSalaries;
            report.NetWorth = report.Funds + report.InventoryValue;
            return report;
        }
    }
}
