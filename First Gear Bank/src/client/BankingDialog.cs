/*
 * Presents the native Vintage Story banking ledger for one local BankingClient conversation.
 * The controller supplies server-generated views, pending status, and confirmations; this dialog owns only disposable
 * form text, page selection, and GuiComposer resources.  Inputs use decimal text, names, or opaque quote intent.
 * No widget changes inventory, accrues interest, estimates an authoritative payoff, or creates an account.
 *
 * Fixed-size restrained panels keep each server page bounded.  History and certificates page by two source records;
 * cumulative totals page independently.  Confirmations replace the form, and all action callbacks recheck busy state
 * so double clicks cannot queue multiple financial commands.  Rebuilds are deferred by the controller until engine
 * input iteration ends, preserving typed form values across transport updates.  Closing only relinquishes UI interest.
 *
 * Banker spawning, native entity dialogue assets, printing, and branch management remain content/server
 * responsibilities.  This ledger supplies localized Banker framing and screens through the registered interaction hook.
 */

using System;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace FirstGearBank.Client;

/// Native ledger window borrowing the client controller; every field here is local presentation state, not authority.
internal sealed class BankingDialog : GuiDialog
{
    private readonly BankingClient client;
    private string page = "account";
    private string amount = "";
    private string recipient = "";
    private string currency = "Rusty";
    private string cashAction = "previewDeposit";
    private bool allAtCommit;
    private int tenor;
    private int namesOffset;
    private int totalsOffset;
    private float scrollContentHeight;
    private float scrollVisibleHeight;
    internal int StatementOffset { get; set; }
    public override string ToggleKeyCombinationCode => null!;



    //// Borrows the controller and builds a closed initial composer so the engine can later open this dialog safely.
    //// The controller, not GuiDialog, owns channel lifetime and money requests.
    ////
    internal BankingDialog(ICoreClientAPI api, BankingClient client) : base(api)
    {
        this.client = client;
        Rebuild();
    }



    //// Clears form and page state between conversations without changing any server-owned account data.
    ////
    internal void Reset()
    {
        page = "account";
        amount = recipient = "";
        currency = "Rusty";
        cashAction = "previewDeposit";
        allAtCommit = false;
        tenor = namesOffset = totalsOffset = StatementOffset = 0;
    }



    //// Recreates a bounded native panel from current display state, retaining typed values in dialog fields.
    //// Buttons remain guarded while transport is outstanding; retry and close intentionally remain available.
    ////
    internal void Rebuild()
    {
        scrollVisibleHeight = 0;
        var background = ElementBounds.Fixed(0, 0, 700, 510).WithFixedPadding(GuiStyle.ElementToDialogPadding);
        var composer = capi.Gui.CreateCompo("firstgearbank-ledger", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background).AddDialogTitleBar(BankingDisplay.Text("title"), CloseWindow)
            .BeginChildElements(background);
        var tabs = new[] { "account", "cash", "transfer", "certificates", "history", "totals" };
        for (var index = 0; index < tabs.Length; index++)
        {
            var tab = tabs[index];
            Button(composer, tab, 110 * index, 35, () => SelectPage(tab), 104);
        }
        Label(composer, client.Status, 0, 77, 690, 60);
        if (client.Confirmation is { } confirmation)
        {
            ScrollText(composer, confirmation.Text, 145, 280);
            Button(composer, "confirm", 0, 437, client.Confirm, 160);
            Button(composer, "cancel", 175, 437, client.CancelConfirmation, 160);
        }
        else if (page == "cash") CashForm(composer);
        else if (page == "transfer") TransferForm(composer);
        else if (page == "certificates") CertificatesForm(composer);
        else
        {
            var text = page switch
            {
                "history" => BankingDisplay.History(client.Display),
                "totals" => BankingDisplay.Totals(client.Display, totalsOffset),
                _ => BankingDisplay.Account(client.Display, client.Session?.DisplayPrecision ?? 3)
            };
            ScrollText(composer, text, 143, 280);
            if (page is "history" or "totals") PageButtons(composer, 437);
            else
            {
                Button(composer, "refresh", 0, 437, RefreshStatement, 160);
                Button(composer, "print", 175, 437,
                    () => client.Read(new("previewPrintStatement")), 160);
            }
        }
        Button(composer, "retry", 475, 478, client.Retry, 104, true);
        Button(composer, "close", 585, 478, CloseWindow, 104, true);
        composer.EndChildElements().Compose();
        if (scrollVisibleHeight > 0)
            composer.GetScrollbar("ledger-scroll").SetHeights(scrollVisibleHeight, scrollContentHeight);
        // The engine's composer setter does not dispose the previous resource; release its textures explicitly.
        SingleComposer?.Dispose();
        SingleComposer = composer;
    }



    //// Keeps long localized ledger rows and full confirmation names readable inside one clipped scrolling region.
    //// Dynamic text renders separately from the background, allowing scroll movement without rebuilding the dialog.
    ////
    private void ScrollText(GuiComposer composer, string text, double y, double height)
    {
        var font = CairoFont.WhiteSmallText().WithFontSize(14);
        scrollVisibleHeight = (float)height;
        var scale = RuntimeEnv.GUIScale;
        scrollContentHeight = Math.Max((float)height,
            (float)(new TextDrawUtil().GetMultilineTextHeight(font, text, 655 * scale) / scale) + 8);
        var bounds = ElementBounds.Fixed(0, y, 655, scrollContentHeight);
        composer.BeginClip(ElementBounds.Fixed(0, y, 665, height))
            .AddDynamicText(text, font, bounds, "ledger-text").EndClip()
            .AddVerticalScrollbar(value =>
            {
                bounds.fixedY = y - value;
                bounds.CalcWorldBounds();
            }, ElementBounds.Fixed(672, y, 16, height), "ledger-scroll");
    }



    //// Adds restrained plain text with explicit bounds; external names were sanitized by the display formatter.
    ////
    private static void Label(GuiComposer composer, string text, double x, double y, double width, double height)
    {
        composer.AddStaticText(text, CairoFont.WhiteSmallText().WithFontSize(14), ElementBounds.Fixed(x, y, width, height));
    }



    //// Adds a guarded action button; transport authority is rechecked inside the controller even after this UI guard.
    ////
    private void Button(GuiComposer composer, string label, double x, double y, Action action, double width = 140,
        bool allowBusy = false)
    {
        composer.AddSmallButton(BankingDisplay.Text(label), () =>
        {
            if (allowBusy || !client.Busy) action();
            return true;
        }, ElementBounds.Fixed(x, y, width, 25));
    }



    //// Adds a bounded editable value and restores its saved form text without changing any confirmation payload.
    ////
    private static void Input(GuiComposer composer, string key, string value, double x, double y, double width,
        int limit, Action<string> changed)
    {
        composer.AddTextInput(ElementBounds.Fixed(x, y, width, 30), changed, CairoFont.TextInput(), key);
        composer.GetTextInput(key).SetMaxLength(limit);
        composer.GetTextInput(key).SetValue(value);
    }



    //// Switches views without losing entered intent and requests a fresh server statement for ledger-backed pages.
    ////
    private void SelectPage(string selected)
    {
        client.CancelConfirmation();
        page = selected;
        if (page is "account" or "history" or "certificates" or "totals") RefreshStatement();
        client.Refresh();
    }



    //// Requests the bounded shared statement offset; a missing or retired conversation cannot query an account.
    ////
    private void RefreshStatement()
    {
        if (client.Scope == Guid.Empty) return;
        client.Read(new("statement", Offset: StatementOffset, Limit: BankingClient.PageSize));
    }



    //// Collects decimal cash intent and asks the server to preflight the complete deposit or withdrawal for review.
    //// Max is a separate server query; the client never estimates account balance or inventory capacity.
    ////
    private void CashForm(GuiComposer composer)
    {
        Label(composer, BankingDisplay.Text("cash-help"), 0, 145, 680, 80);
        composer.AddDropDown(["Rusty", "Temporal"], [BankingDisplay.Text("Rusty"), BankingDisplay.Text("Temporal")],
            currency == "Rusty" ? 0 : 1, (code, _) => currency = code, ElementBounds.Fixed(0, 235, 215, 30));
        composer.AddDropDown(["previewDeposit", "previewWithdraw"],
            [BankingDisplay.Text("deposit"), BankingDisplay.Text("withdraw")], cashAction == "previewDeposit" ? 0 : 1,
            (code, _) => cashAction = code, ElementBounds.Fixed(235, 235, 215, 30));
        Label(composer, BankingDisplay.Text("amount"), 0, 289, 160, 25);
        Input(composer, "amount", amount, 160, 282, 290, 32, value => amount = value);
        Button(composer, "review", 0, 345, () => client.Read(new(cashAction, Amount: amount, Currency: currency)), 180);
        if (cashAction == "previewWithdraw")
            Button(composer, "max", 195, 345,
                () => client.Read(new("previewWithdrawMax", Currency: currency)), 180);
    }



    //// Collects a name-facing rusty transfer with explicit exact/all-at-commit intent and optional bounded discovery.
    //// The selected canonical recipient and amount are shown only after the server resolves a confirmation token.
    ////
    private void TransferForm(GuiComposer composer)
    {
        Label(composer, BankingDisplay.Text("transfer-help"), 0, 140, 680, 50);
        Label(composer, BankingDisplay.Text("recipient"), 0, 203, 140, 25);
        Input(composer, "recipient", recipient, 145, 195, 350, 64, value => recipient = value);
        composer.AddDropDown(["exact", "all"], [BankingDisplay.Text("exact-mode"), BankingDisplay.Text("all-mode")],
            allAtCommit ? 1 : 0, (code, _) => { allAtCommit = code == "all"; client.Refresh(); },
            ElementBounds.Fixed(0, 242, 240, 30));
        Input(composer, "amount", amount, 260, 242, 235, 32, value => amount = value);
        Button(composer, "review", 510, 245,
            () => client.Read(new("confirmTransfer", Amount: allAtCommit ? null : amount, Name: recipient)), 175);
        if (client.Session?.RecipientMode != "KnownPlayerListing") return;
        Button(composer, "search", 0, 297, () => { namesOffset = 0; SearchNames(); }, 130);
        Button(composer, "previous", 145, 297,
            () => { namesOffset = Math.Max(0, namesOffset - 5); SearchNames(); }, 130);
        Button(composer, "next", 290, 297,
            () => { if (client.Names.Length == 5) { namesOffset += 5; SearchNames(); } }, 130);
        // Selecting a discovered name only fills the form; it cannot authorize or submit a transfer.
        for (var index = 0; index < Math.Min(5, client.Names.Length); index++)
        {
            var name = client.Names[index];
            composer.AddSmallButton(BankingDisplay.Safe(name), () =>
            {
                if (!client.Busy) { recipient = name; client.Refresh(); }
                return true;
            }, ElementBounds.Fixed(0, 335 + index * 27, 495, 23));
        }
    }



    //// Requests a five-name discovery page under the server's recipient mode without revealing account data.
    ////
    private void SearchNames()
    {
        client.Read(new("names", Name: recipient, Offset: namesOffset, Limit: 5));
    }



    //// Shows active contracts separately from the new-offer inputs, using only server-advertised tenor choices.
    //// A quote still validates current settings on the server; opening metadata is not permanent eligibility.
    ////
    private void CertificatesForm(GuiComposer composer)
    {
        Label(composer, BankingDisplay.Text("cd-help", BankingDisplay.Exact(client.Session?.MinimumCdPrincipalUnits ?? 0)),
            0, 140, 690, 50);
        Input(composer, "amount", amount, 0, 196, 215, 32, value => amount = value);
        var tenors = client.Session?.TenorsMonths ?? [];
        if (tenors.Length > 0)
        {
            tenor = Math.Clamp(tenor, 0, tenors.Length - 1);
            var values = tenors.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
            composer.AddDropDown(values, values.Select(value => BankingDisplay.Text("months", value)).ToArray(), tenor,
                (code, _) => tenor = Array.IndexOf(values, code), ElementBounds.Fixed(235, 196, 195, 30));
            Button(composer, "quote", 450, 199,
                () => client.Read(new("quoteCd", Amount: amount, TenorMonths: tenors[tenor])), 235);
        }
        ScrollText(composer, BankingDisplay.Certificates(client.Display), 248, 180);
        PageButtons(composer, 437);
    }



    //// Pages server collections by source-record count and totals locally; an empty last page is harmless.
    //// CD/history totals remain independent even though their server request shares one offset.
    ////
    private void PageButtons(GuiComposer composer, double y)
    {
        Button(composer, "previous", 0, y, () => TurnPage(-1), 140);
        Button(composer, "next", 155, y, () => TurnPage(1), 140);
        Button(composer, "refresh", 310, y, RefreshStatement, 140);
        Label(composer, BankingDisplay.Text("page", (page == "totals" ? totalsOffset / 6 :
            StatementOffset / BankingClient.PageSize) + 1), 480, y + 5, 180, 25);
    }



    //// Advances a bounded list only when the current server snapshot reports additional source entries.
    ////
    private void TurnPage(int direction)
    {
        if (page == "totals")
        {
            var next = Math.Max(0, totalsOffset + direction * 6);
            if (next < (client.Display?.Statement.Totals.Count ?? 0) || next == 0) totalsOffset = next;
            client.Refresh();
            return;
        }
        var offset = Math.Max(0, StatementOffset + direction * BankingClient.PageSize);
        var total = page == "certificates" ? client.Display?.TotalCertificates ?? 0 :
            client.Display?.Statement.TotalHistoryRows ?? 0;
        if (offset >= total && offset != 0) return;
        StatementOffset = offset;
        RefreshStatement();
    }



    //// Rebuilds current state immediately when the engine opens a previously hidden dialog.
    ////
    public override void OnGuiOpened()
    {
        Rebuild();
        base.OnGuiOpened();
    }



    //// Forwards Escape/title-bar closure to the controller without pretending to cancel sent transactions.
    ////
    public override void OnGuiClosed()
    {
        client.Close();
        base.OnGuiClosed();
    }



    //// Adapts title-bar and footer callbacks to the engine's ordinary dialog-closing lifecycle.
    ////
    private void CloseWindow()
    {
        TryClose();
    }



}
