<h1>Flow Launcher - Bitwarden Vault Plugin</h1>

This is a Flow Launcher plugin for Bitwarden - an application I like to use for password management. I noticed there wasn't a plugin to provide this functionality, so I thought I'd make one myself.<br><br>
This is my first ever Flow Launcher plugin, so I am open to suggestions/critique on ways I can improve this.

<h2>Features</h2>
As of this release, it contains the following capabilities:<br><br>

1) Fuzzy search across your vault via the bw command with icon population of individual vault items based on the URIs you have saved in your individual vault items.
2) Icon caching for faster population of visuals.
3) The ability to copy usernames, passwords, and URI's for each vault item with an expander for URI items in case you have multiple listed that you can select from.
4) An adjustable log that defaults to showing only Warning and Error messages, but can be expanded to show additional logging from the Flow Launcher settings menu.

<h2>Requirements</h2>
In order to use this plugin, you must have the <a href="https://bitwarden.com/help/cli/">Bitwarden CLI installed</a>.

<h2>Installation Instructions</h2>
I'm aware of the documentation on how to add a plugin to the Flow Launcher plugin store, but I'm nervous to publish since it's my first plugin and want to make sure there isn't anything generally wrong with the plugin before doing so (like if there's any security concerns and whatnot with my code), so for now, here's some simple instructions on how to get it working
with your current Flow Launcher installation:<br><br>

1) Make sure you've installed the Bitwarden CLI as indicated in the Requirements section above.
2) Download the latest release.zip file in the release section here.
3) Extract the contents to your Flow Launcher Plugins directory (installation location may vary depending on how you installed Flow Launcher. The easiest way to figure this out is to go to your Flow Launcher Plugin Settings page, pick an existing plugin, and navigate to its folder. Going up one folder from that is where it's reading your plugins.)

<h2>How to Use:</h2>
<ul>
  <li>Upon installation of the plugin, you'll need to configure the plugin settings and provide the following attributes:</li>
  <ul>
    <li>Your Bitwarden <code>client_id</code> from your respective Bitwarden API menu</li>
    <li>Your Bitwarden <code>client_secret</code> from your respective BitwardenAPI menu</li>
    <ul>
      <li>Instructions for the API stuff can be found in the plugin settings menu.</li>
    </ul>
    <li>Your Bitwarden master password.</li>
  </ul>
  <li>Type <code>bw</code> before your search-term to start populating items in your vault.
  <li>Just pressing <code>ENTER</code> on the populated item will copy the password.</li>
  <li>Pressing <code>CTRL</code>+<code>ENTER</code> copies the username.</li>
  <li>Pressing <code>CTRL</code>+<code>SHIFT</code>+<code>ENTER</code> brings up the URI menu where you can use the <code>UP</code> and <code>DOWN</code> arrow keys to select your preferred URI and press <code>ENTER</code> to copy it to your clipboard.</li>
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
    <img src="/screenshots/Flow.Launcher_RtNvIxkhtk.png" width="400px"</img> 
    <p>Flow Launcher plugin settings screen.</p>
</div>
