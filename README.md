<h1>Flow Launcher - Bitwarden Vault Plugin</h1>

This is a Flow Launcher plugin for Bitwarden - an application I like to use for password management. I noticed there wasn't a plugin to provide this functionality, so I thought I'd make one myself.<br><br>
This is my first ever Flow Launcher plugin, so I am open to suggestions/critique on ways I can improve this.

<h2>Features</h2>
As of this release (<b>1.3.1</b>), it contains the following capabilities:<br><br>

1) Fuzzy search across your vault via the bw command with icon population of individual vault items based on the URIs you have saved in your individual vault items.
2) Icon caching for faster population of visuals.
3) The ability to copy usernames, passwords, TOTPs, and URI's for each vault item with an expander for URI items in case you have multiple listed that you can select from.
4) An adjustable log that defaults to showing only Warning and Error messages, but can be expanded to show additional logging from the Flow Launcher settings menu.
5) Adjustable notification settings for copy settings
   - **NEW** - Additional adjustable notification settings for sync status</li>
7) Adjustable vault auto-lock timeouts ranging from letting you keep your vault unlocked, or setting it to lock automatically after however many minutes.
8) **NEW** - Adjustable clipboard clearing settings that mirror the official Bitwarden client. Defaults to 'None'.
9) **NEW** - Manual sync - updated a URI for a vault item you don't have an icon for and want to refresh it? Added a new account and want to immediately see it in your search from Flow Launcher? Manually run the <code>/sync</code> command to immediately sync your vault and icons.
    - This also occurs after each unlock asynchronously with the unlock process so there shouldn't be any delay beyond the initial unlock.
9) **NEW** - CLI setting assist now exists in the plugin settings. Just download the CLI into a location of your choosing and use the file picker in the plugin settings to point to it and set your user PATH variable to include this PATH automatically. No more hunting around in Windows settings yourself to set this stuff.
    - Even if you've already done this process in previous versions, it is advised to still point to your bw.exe file in the plugin settings. It will automatically detect if you've already added it to your PATH variable in either SYSTEM or USER settings.

<h2>Requirements</h2>
In order to use this plugin, you have to have the <a href="https://bitwarden.com/help/cli/#download-and-install">Bitwarden CLI downloaded</a>.<br>
<li>You <b>MUST</b> use version 2024.7.2 or later - otherwise the API login process is broken on older versions of the CLI.</li>
<li><b>NEW</b> - You can now use the settings menu for the plugin to help set your environment variables if you navigate to your bw.exe file on your computer using the included file picker.</li>

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
    </ul>
    <li>Your Bitwarden <code>client_id</code> from your respective Bitwarden API menu</li>
    <li>Your Bitwarden <code>client_secret</code> from your respective BitwardenAPI menu</li>
    <ul>
      <li>Instructions for the API stuff can be found in the plugin settings menu.</li>
    </ul>
    <li>Adjust your desired settings for logging, notifications, vault unlock timeout, and automatic clipboard clearing settings.
    <li><s>You will need to restart Flow Launcher in order for the credentials in the plugin settings to take effect. This is not required when changing any other setting in the plugin.</s>
    <ul>
      <li>This is no longer required! It should immediately start working after you've provided your API credentials.</li>
    </ul>
  </ul>
  <li>Type <code>bw</code> before your search-term to start populating items in your vault.
  <ul>
    <li>If your vault is locked, you will be prompted to unlock it first with the <code>/unlock</code> sub-command to continue.
    <li>You will be notified when your vault is unlocked and ready to be used with a OS notification.
    <ul>
      <li>If you have sync status notifications enabled, as soon as you see the notification that your vault is syncing, it is ready to use.
      <li>Otherwise, if you have those notifications turned off, you'll just get the standard 'vault is unlocked' notification and you'll know it's ready.
    </ul>
  <li>Adding sub-command <code>/lock</code> or <code>/unlock</code> after <code>bw</code> (make sure to include a space between them) will allow you to manually lock and unlock the vault respectively.
  <li>Adding sub-command <code>/sync</code> will sync your vault items and their respective icons. 
  <li>Just pressing <code>ENTER</code> on the populated item will copy the password.</li>
  <li>Pressing <code>CTRL</code>+<code>ENTER</code> copies the username.</li>
  <li>Pressing <code>CTRL</code>+<code>SHIFT</code>+<code>ENTER</code> brings up the URI menu where you can use the <code>UP</code> and <code>DOWN</code> arrow keys to select your preferred URI and press <code>ENTER</code> to copy it to your clipboard.</li>
  <li>Pressing <code>CTRL</code>+<code>T</code>+<code>ENTER</code> copies the TOTP.
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
