<h1>Flow Launcher - Bitwarden Vault Plugin</h1>

<h3>This project is officially archived - I would not recommend using it in its current state as I can no longer maintain it as I no longer have the time nor use Bitwarden as a password manager these days.
<br><br>Please feel free to fork it and use it as your own, tweak it, modify it, add features, and submit it to the Flow Launcher repository. I'm sorry for any inconvenience this may cause.</h3>
<h2></h2>

This is a Flow Launcher plugin for Bitwarden - an application I like to use for password management. I noticed there wasn't a plugin to provide this functionality, so I thought I'd make one myself.<br><br>
This is my first ever Flow Launcher plugin, so I am open to suggestions/critique on ways I can improve this.

<h2>Features</h2>
As of this release (<b>2.1.0</b>), it contains the following capabilities:<br><br>

1) Search across your entire vault from within Flow Launcher.
   - Icon population for each vault item if you have a URL associated with that item and it resolves and can produce a favicon.
   - Copy common vault item values including username, password, TOTPs, and URLs.
   - Icon and vault item caching so searching across your vault using Flow Launcher has never been faster.
     - Sensitive information for your vault is not stored in the cache. Information stored specifically are what you can see in the item results of Flow Launcher including username, URLs, and whether or not your vault item has a TOTP code associated with it.
2) Self-hosted server support.
   - Self-host your own instance of Bitwarden or Vaultwarden? Enter your server information from within the plugin settings to connect to your own instance.
3) Adjustable autolock parameters to securely lock your vault after a period of inactivity of your choosing.
4) Adjustable clipboard history control to clear your clipboard after a period of time of your choosing.
5) Customizable notifications.
   - Adjust notifications for vault item copying actions, sync actions, auto-lock, etc. from within the plugin settings.
6) Automatic download, extraction, and configuration of the Bitwarden CLI to a place of your choosing and will configure your PATH variables for you.
7) Shortcut and context menu support - can use keyboard shortcuts to perform copy actions or use Flow Launcher's built-in context menus on your vault items to copy what you want.
8) Re-prompt for master password on vault items configured for it.

<h2>Requirements</h2>
In order to use this plugin, you have to have the <a href="https://bitwarden.com/help/cli/#download-and-install">Bitwarden CLI downloaded</a>.<br>
<li>Please ensure you download the latest version as there are login issues with previous versions. If you're not sure where to go, download it direct from the settings screen for the plugin.</li>

<h2>Installation Instructions</h2>
Install from the Plugin Store from the Flow Launcher settings menu or type the following in Flow Launcher 😊:<br><br>

<code>pm install bitwarden</code>

<h2>How to Use:</h2>
<ul>
  <li>Upon installation of the plugin, you'll need to configure the plugin settings and provide the following attributes:</li>
  <ul>
    <li>Your Bitwarden CLI executable location. 
    <ul>
      <li>You should keep this file named bw.exe so command references work correctly.</li>
      <li>Make sure to click the text to add the path to your user <code>PATH</code> environment variable if it isn't already there.</li>
      <li>If this is a fresh installation of the plugin and you've never downloaded the CLI before, just download it from the settings menu. Once you pick a place for it to go it'll handle all the system configurations.</li>
    </ul>
    <li>If you're using a self-hosted instance, you'll need to check the box to use self-hosted instance and provide at a bare minimum, the URL to your self-hosted instance.</li>
    <ul>
        <li>You'll want to do this before you set your <code>client_id</code> and <code>client_secret</code>, or else when you check the box to use a self-hosted instance, it's going to clear your entries.</li>
        <li>If you have some additional advanced configurations, you can expand the optional advanced settings dropdown to populate various additional attributes about your instance.</li>
        <li>Make sure to click the 'Apply Server Configuration' button to apply your changes.</li>
    </ul>
    <li>Your Bitwarden <code>client_id</code> from your respective Bitwarden API menu</li>
    <li>Your Bitwarden <code>client_secret</code> from your respective Bitwarden API menu</li>
    <ul>
      <li>Instructions for the API stuff can be found in the plugin settings menu.</li>
    </ul>
    <li>Adjust your desired settings for logging, notifications, vault unlock timeout, and automatic clipboard clearing settings.</li>
    </ul>
  </ul>
  <li>Type <code>bw</code> before your search-term to start populating items in your vault.</li>
  <ul>
    <li>If your vault is locked, you will be prompted to unlock it first. Press <code>ENTER</code> or click on the item and you'll be prompted for your master password. Press <code>ENTER</code> again at the new window after you've supplied it.</li>
    <li>You will be notified when your vault is unlocked and ready to be used with a OS notification.</li>
    <ul>
      <li>If you have sync status notifications enabled, as soon as you see the notification that your vault is syncing, it is ready to use.</li>
      <li>Otherwise, if you have those notifications turned off, you'll just get the standard 'vault is unlocked' notification and you'll know it's ready.</li>
    </ul>
  <li>To manually lock or sync your vault, open the context menu immediately after tying in <code>bw</code> without a search term and that's where you'll find them.</li>
  <li>Just pressing <code>ENTER</code> on the populated item will copy the password.</li>
  <li>Pressing <code>CTRL</code>+<code>ENTER</code> copies the username.</li>
  <li>Pressing <code>CTRL</code>+<code>SHIFT</code>+<code>ENTER</code> brings up the URI menu where you can use the <code>UP</code> and <code>DOWN</code> arrow keys to select your preferred URI and press <code>ENTER</code> to copy it to your clipboard.</li>
  <li>Pressing <code>CTRL</code>+<code>T</code>+<code>ENTER</code> copies the TOTP.
  <li>If you don't want to remember shortcuts, each vault item has context menus for each operation.</li>
</ul>

<h2>Screenshots</h2>
<div align="center">
    <img src="/screenshots/Flow.Launcher_YiU6ftN0Sc.png" width="400px"</img> 
    <p>Initial look after typing in the keyword.</p>
</div>
<div align="center">
    <img src="/screenshots/Flow.Launcher_I4Vy1Xpcrz.png" width="400px"</img> 
    <p>Some search results.</p>
</div>
<div align="center">
    <img src="/screenshots/Flow.Launcher_VhOPuemKFn.png" width="400px"</img> 
    <p>Multiple search results.</p>
</div>
<div align="center">
    <img src="/screenshots/Flow.Launcher_RtNvIxkhtk.png" width="400px"</img> 
    <p>Flow Launcher plugin settings screen.</p>
</div>
<div align="center">
   <img src="/screenshots/Flow.Launcher_jzUEnY2LBl.png" width="400px"</img>
   <p>Context menu for vault item example.</p>
</div>
