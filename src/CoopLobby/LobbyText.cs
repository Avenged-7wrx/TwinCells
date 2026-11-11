using System;
using System.Collections.Generic;

// Lobby strings in the game's language. The current language is read live (dc.Lang.Class.LANG, canonical ids:
// en fr de es-es es pl it ru pt tr zh ko zh-tw ja; workshop ids look like "<code>:<item>"). Our own table has
// en and ru; every other language uses en for our own words. Generic words take the game's own translation
// (French msgids, present in all 13 shipped .mo files) so they match the rest of the game's UI.
// Keep every string inside the game fonts' glyph sets: no em dash, ellipsis, NBSP, bullets or check marks.
public static class LobbyText
{
    static readonly Dictionary<string,(string En,string Ru)> T=new()
    {
        ["title"]=("Co-op lobby","Кооп-лобби"),
        ["players"]=("Players: {0} of 2","Игроков: {0} из 2"),
        ["hostedBy"]=("Host: {0}","Хост: {0}"),
        ["waiting"]=("Waiting for player","Ожидание игрока"),
        ["connecting"]=("Connecting","Подключение"),
        ["ready"]=("Ready","Готов"),
        ["notReady"]=("Not ready","Не готов"),
        ["host"]=("Host","Хост"),
        ["hostYou"]=("Host (you)","Хост (вы)"),
        ["you"]=("You","Вы"),
        ["player2"]=("Player 2","Игрок 2"),
        ["color"]=("Colour","Цвет"),
        ["colorTaken"]=("Colour taken by {0}","Цвет занят: {0}"),
        ["colorPending"]=("Waiting for the host","Ждём ответа хоста"),
        ["colorUnsupported"]=("Your partner's game has no colour choice","У друга нет выбора цвета"),
        ["lobbyFull"]=("Lobby is full","Лобби заполнено"),
        ["lobbyFullText"]=("Only 2 players can play in this game.","В этой игре могут играть только 2 игрока."),
        ["lobbyFullHost"]=("A third player tried to join: the lobby is full","Третий игрок пытался подключиться: лобби заполнено"),
        ["newGame"]=("New game","Новая игра"),
        ["disconnect"]=("Disconnect","Отключиться"),
        ["invite"]=("Invite friends","Пригласить друзей"),
        ["hostMode"]=("Host mode: {0}","Режим хоста: {0}"),
        ["allReady"]=("Both players are ready","Оба готовы, можно запускать"),
        ["notAllReady"]=("Launch unlocks when both players are ready","Запуск откроется, когда оба будут готовы"),
        ["waitHost"]=("Waiting for the host to launch","Ждём, пока хост запустит игру"),
        ["toggleReady"]=("Toggle your ready state","Переключить свою готовность"),
        ["saveSlotHelp"]=("Co-op save slot","Слот сохранения для кооператива"),
        ["backHelp"]=("Close the lobby","Закрыть лобби"),
        ["disconnectHelp"]=("Leave and return to join options","Выйти и вернуться к подключению"),
        ["continueHelp"]=("Continue the shared run","Продолжить общий забег"),
        ["customHelp"]=("Configure and launch custom mode","Настроить и запустить особый режим"),
        ["inviteHelp"]=("Open the Steam friends list","Открыть список друзей Steam"),
        ["codeHelp"]=("Click to copy the code","Нажмите, чтобы скопировать код"),
        ["colorUnavailable"]=("Colour not available now","Цвет сейчас недоступен"),
        ["code"]=("Lobby code: {0}","Код лобби: {0}"),
        ["codeCopied"]=("Code copied","Код скопирован"),
        ["color.ember"]=("Orange","Оранжевый"),
        ["color.ice"]=("Turquoise","Бирюзовый"),
        ["color.violet"]=("Violet","Фиолетовый"),
        ["color.moss"]=("Green","Зелёный"),
        ["color.rose"]=("Pink","Розовый"),
        ["color.azure"]=("Blue","Синий"),
        // the other Multiplayer pages (UI part, LobbyUiPageRules keys)
        ["mpTitle"]=("Co-op multiplayer","Кооператив"),
        ["mpSub"]=("Host a game or join a friend over LAN or Steam","Создайте игру или подключитесь к другу по LAN или Steam"),
        ["hostGame"]=("Host game","Создать игру"),["hostGameHelp"]=("Start a co-op session","Начать кооперативную сессию"),
        ["joinGame"]=("Join game","Подключиться"),["joinGameHelp"]=("Join a friend's game","Войти в игру друга"),
        ["backToTitle"]=("Return to the main menu","Вернуться в главное меню"),
        ["hostOptions"]=("Host a game","Создание игры"),
        ["hostOptionsSub"]=("LAN uses IP and port. Steam uses friends or a lobby code.","LAN: по IP и порту. Steam: друзья или код лобби."),
        ["lanHost"]=("LAN","LAN"),["lanHostHelp"]=("Host by IP and port","Игра по IP и порту"),
        ["steamHost"]=("Steam","Steam"),["steamHostHelp"]=("Choose who can find the lobby","Выбрать, кто найдёт лобби"),
        ["backToMp"]=("Back to the co-op menu","Назад в меню кооператива"),
        ["steamVis"]=("Steam lobby","Лобби в Steam"),
        ["steamVisSub"]=("Friends only stays private. Public gives a lobby code to share.","Только друзья: закрытое лобби. Публичное: с кодом для входа."),
        ["steamFriends"]=("Friends only","Только друзья"),["steamFriendsHelp"]=("Your Steam friends can see it","Лобби видят только друзья в Steam"),
        ["steamPublic"]=("Public","Публичное"),["steamPublicHelp"]=("Open lobby with a code to share","Открытое лобби с кодом для входа"),
        ["backToHost"]=("Back to the hosting options","Назад к способам создания"),
        ["joinTitle"]=("Join a game","Подключение"),
        ["joinSubFriends"]=("Friends hosting right now are listed below","Ниже друзья, которые сейчас создали игру"),
        ["joinSubNone"]=("No friend is hosting right now. The list refreshes by itself.","Сейчас никто из друзей не создал игру. Список обновляется сам."),
        ["lanJoin"]=("LAN","LAN"),["lanJoinHelp"]=("Connect by IP and port","По IP и порту"),
        ["steamOverlay"]=("Steam friends","Друзья Steam"),["steamOverlayHelp"]=("Open the Steam friends list","Открыть друзей в Steam"),
        ["codeJoin"]=("Lobby code","Код лобби"),["codeJoinHelp"]=("Code from the clipboard","Код из буфера обмена"),
        ["joinFriend"]=("Join","Войти"),["joinFriendHelp"]=("Join this friend's Steam lobby","Войти в лобби друга в Steam"),
        ["noFriendLobbies"]=("No friend lobbies yet","Пока нет лобби друзей"),
        ["pageOf"]=("{0} / {1}","{0} / {1}"),["pagePrev"]=("Previous page","Предыдущая страница"),["pageNext"]=("Next page","Следующая страница"),
        ["backToJoin"]=("Back to the join options","Назад к способам подключения"),
        ["lanHostTitle"]=("LAN game: host","Игра по LAN: хост"),
        ["lanHostSub"]=("Tell the other player your IP and port (same network)","Сообщите другому игроку свой IP и порт (одна сеть)"),
        ["lanJoinTitle"]=("LAN game: join","Игра по LAN: подключение"),
        ["lanJoinSub"]=("Enter the host's IP and port, then connect","Введите IP и порт хоста и подключитесь"),
        ["fieldUser"]=("Player name","Имя игрока"),["fieldIp"]=("IP address","IP-адрес"),["fieldPort"]=("Port","Порт"),
        ["editUser"]=("Change your name","Изменить имя"),["editIp"]=("Change the IP address","Изменить IP-адрес"),["editPort"]=("Change the port","Изменить порт"),
        ["startHost"]=("Host","Создать"),["startHostHelp"]=("Start hosting","Начать игру как хост"),
        ["connect"]=("Connect","Подключиться"),["connectHelp"]=("Connect to the host","Подключиться к хосту"),
        // fallbacks for the game words below, used only when the game has no translation
        ["back"]=("Back","Назад"),["continue"]=("Continue","Продолжить"),["normalMode"]=("Normal mode","Нормальный режим"),
        ["customMode"]=("Custom mode","Особый режим"),["slot"]=("Slot {0}","Слот {0}"),
    };
    // our key -> the game's French msgid
    static readonly Dictionary<string,string> Game=new()
    {
        ["back"]="Retour",["continue"]="Continuer",["normalMode"]="Mode normal",["customMode"]="Mode personnalisé",
    };

    public static string Lang
    {
        get
        {
            try
            {
                var s=dc.Lang.Class.LANG?.ToString()??"en";
                int colon=s.IndexOf(':');if(colon>0)s=s.Substring(0,colon);
                return s.ToLowerInvariant();
            }
            catch{return "en";}
        }
    }
    public static bool Russian=>Lang=="ru";

    public static string Get(string key)
    {
        if(Game.TryGetValue(key,out var msgid)){var g=GameString(msgid);if(g!=null)return g;}
        if(!T.TryGetValue(key,out var v))return key;
        return Russian?v.Ru:v.En;
    }
    public static string Format(string key,params object[] args)
    {
        if(key=="slot"&&args.Length==1)
        {
            var g=GameString("Slot ::index::");
            if(g!=null&&g.Contains("::index::"))return g.Replace("::index::",Convert.ToString(args[0]));
        }
        try{return string.Format(Get(key),args);}catch{return Get(key);}
    }
    // The game's translation of a French msgid, or null when this language has none.
    public static string GameString(string msgid)
    {
        try
        {
            var s=ModCore.Modules.GetText.Instance.GetString(msgid);
            if(string.IsNullOrEmpty(s))return null;
            if(s==msgid&&Lang!="fr")return null;    // missing keys come back unchanged
            return s;
        }
        catch{return null;}
    }
}
