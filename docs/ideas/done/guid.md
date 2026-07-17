### 9.1 GUID soul IDs

`User.Id` and every foreign-key `UserId` column (15 tables in `Aria.Web`, plus
`BridgeSoul.ServerSoulId` and the SignalR/REST wire formats) changed from sequential `int` to a
`Guid.NewGuid().ToString()` **string**. `register-soul` now inserts an explicit GUID instead of
relying on `last_insert_rowid()`.

It used to be an int, now its a string (GUID)

## Chat note

 ---                                                                                                                                             
  Root cause (two layers)                                                                                                                         
                                                                                                                                                  
  1. Bridge auth failure (data issue): The bridge's SQLite DB still holds ServerSoulId = "9" (the old integer user ID). After the GUID migration
the server's Users table uses GUID primary keys, so RegisterDirectBridge("9", ...) can't find a matching user → "No public key on record for    
  userId=9" → bridge disconnects.                                                                                                                 
    - Without a connected bridge, WaitForBridgeIfNeeded() in OnNewChatRequestedAsync returns true and blocks new-chat init forever.               
    - This forces the user into the cogitation-reopen path (OnCogitationSelected) for every chat.                                                 
  2. Missing tool callbacks in session creation (code bug — this file): onToolStart/onToolComplete were only wired in InitAgentAsync. The three   
  other CreateSessionAsync call-sites — OnCogitationSelected, OnActiveSubAgentUpdated, OnToolSettingsChanged — all passed null, so                
  LoadBridgeToolsAsync never fired the UI callbacks → no tool-activity sections ever appeared → no tool-call blocks.                              
    - Thinking blocks were not affected by this bug (OnThinkingToken was always passed), but nobody was seeing new chats because of layer 1.      
                                                                                                                                                  
  What's fixed here: extracted the callbacks into OnToolStart / OnToolComplete methods and wired them in all 4 CreateSessionAsync sites.          
                                                                                                                                                  
  You also need to re-link the bridge (data fix, no code change):                                                                                 
  1. Start Aria.Bridge                                                                                                                            
  2. Open http://localhost:5741 and navigate to link-server (or POST /soul/link-server with your server URL)                                      
  3. The server will look up your soul by public key, return the correct GUID as serverSoulId, and store it on the bridge                         
  4. On next connect, bridge sends the GUID → auth succeeds Jump to bottom (ctrl+End) ↓ ocks → new chats work again
──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
❯ tool output is good now, but thinking is still rendering as normal content 