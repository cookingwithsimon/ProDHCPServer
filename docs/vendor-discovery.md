# Phase 3: vendor and industry discovery mechanisms

Research date: 2026-10-16. Every claim marked "confirmed" was read from the vendor's or standards body's own document (URL given). mDNS/DNS-SD and SSDP are already planned and are only covered here where a vendor names a service type.

Status key:
- **Confirmed**: transport, address/port and message format all come from an official source. Can be implemented.
- **Partial**: an official source confirms the address/port (and sometimes the fields), but not the exact wire format. Listen passively and log the raw payload; don't build a parser on guesses.
- **Not confirmed**: no official documentation of a usable format. Dropped.

## Summary table

| Mechanism | Who uses it | Mode | Transport / address / port | Status |
|---|---|---|---|---|
| PJLink Class 2 search | Projectors and displays (Epson, Fujifilm, NEC, Panasonic, Sony etc.) | Active | UDP broadcast to subnet, port 4352; replies unicast to UDP 4352 | **Confirmed** (JBMIA spec) |
| Art-Net ArtPoll | Lighting nodes, DMX gateways, LED processors | Active | UDP directed broadcast, port 6454 (0x1936) | **Confirmed** (Art-Net spec) |
| SAP (AES67 stream announcements) | Dante (AES67 mode), Shure Dante cards, other AES67 gear | Passive | UDP multicast 239.255.255.255:9875 | **Confirmed** (RFC 2974 + Audinate + Shure) |
| AMX Device Discovery beacon (DDP) | AMX controllers' "third party" discovery; beacons from projectors and displays (Optoma etc.) | Passive | UDP multicast 239.255.250.250:9131 | **Partial**: address, port and field names confirmed; exact text framing not in an official doc |
| AMX NetLinx ICSP blink / NDP | AMX NetLinx masters | Passive | UDP broadcast port 1319; NDP multicast 239.255.250.251:1319 | **Partial**: presence only, payload not documented |
| Q-SYS Discovery Protocol (QDP) | Q-SYS Cores, peripherals, Designer PCs | Passive | UDP multicast 224.0.23.175, ports 2467-2470 | **Partial**: payload format not documented |
| Shure SLP | Shure MXA, ANI, P300, ULXD, AD, AXT, SBC etc. | Passive | UDP multicast 239.255.254.253:8427 (SLPv2 framing per RFC 2608) | **Partial**: service type names not documented |
| Biamp Tesira proprietary discovery | Tesira servers and expanders | (Passive) | UDP 12003, TCP/UDP 61451, multicast 224.0.0.252-254 | **Not confirmed**: format proprietary |
| Extron discovery | Extron control processors, Toolbelt | (Active) | UDP broadcast 4502 | **Not confirmed**: format not published |
| Allen & Heath AHNet discovery | dLive, Director, MixPad apps | (Active) | UDP broadcast 51320 | **Not confirmed**: format not published |
| Bose ControlSpace scan | Bose Professional ControlSpace | (Active) | port 9010 "Scan/Discovery" | **Not confirmed**: format not published |
| Crestron autodiscovery | Crestron control systems and devices | — | UDP 41794 (listed only as "control communications") | **Not confirmed**, and Crestron says it allows remote config: **don't send** |
| HiQnet Disco Info | Legacy BSS Soundweb London, Crown, Soundcraft | — | UDP/TCP 3804 | **Not confirmed** to implementation level (payload not extracted); legacy |
| Ubiquiti discovery | UniFi / EdgeMax / airMAX | — | UDP/TCP 10001 | **Not confirmed**: port only, format not official |
| Netgear NSDP | Netgear smart/AV switches | — | — | **Not confirmed**: no official doc; protocol also has write commands |
| Kramer, Lightware, Atlona, Yamaha, Barco ClickShare | — | — | — | **Not confirmed**: no public discovery protocol found (see notes) |

## mDNS service types the vendors document

| Service type | Vendor | Source |
|---|---|---|
| `_ssc._udp` (always), `_ssc._tcp` (if TCP supported) | Sennheiser SSC devices (EW-DX, TCC2, SL/DW, Digital 9000, TeamConnect) | EW-DX SSC doc, s. 6.2 |
| `_dante-ddm-d._udp` (devices), `_dante-ddm-c._tcp` (controllers) | Audinate Dante Domain Manager | Audinate DDM network-admin tech note |
| `_services._dns-sd._udp.local` (meta-query: list every service type present) | Standard (RFC 6763 s. 9) | RFC 6763 |

Vendors that say they use mDNS for discovery but **do not publish service type names**: Q-SYS (hostname resolution on LAN A), Biamp Tesira, Shure (UDP 5353 "Required for device discovery"), Sennheiser Control Cockpit, Barco ClickShare ("mDNS/SSDP"), Crestron AirMedia, Audinate Dante ("mDNS and DNS-SD are used for discovery and enumeration of other Dante devices"), NDI.

**Not confirmed** (widely observed but not found in any Audinate/NDI document, so don't hard-code them as fact): `_netaudio-arc._udp`, `_netaudio-cmc._udp`, `_netaudio-dbc._udp`, `_netaudio-chan._udp` (Dante), `_ndi._tcp` (NDI). Recommendation: send the `_services._dns-sd._udp.local` meta-query and show whatever types come back. That catches all of these without depending on undocumented names. If you want to label known types, mark Dante/NDI labels as "probably" in code comments.

---

## 1. PJLink Class 2 search: confirmed

- **Source:** JBMIA, PJLink Specifications (Class 2), https://pjlink.jbmia.or.jp/english/data_cl2/PJLink_5-1.pdf, section 3.2 "Search protocol". Index: https://pjlink.jbmia.or.jp/english/dl_class2.html. Vendor confirmation: Epson, "The port number used for the PJLink search function is 4352 (UDP)." (https://files.support.epson.com/docid/cpd6/cpd64101/source/network/concepts/pjlink.html)
- **Key quotes:** "A search start command is transmitted to the broadcast address from the controller." / "The controller receives all of the responses from the projectors/displays for 30 seconds." / The device replies "in random time (0 to 10 seconds)". Section 6: search and status notification work without authentication.
- **Who:** any PJLink **Class 2** projector or display. Class 1-only devices won't answer.
- **Mode:** active.
- **Send:** UDP to the adapter's directed broadcast (e.g. 192.168.0.255; IPv6 ff02::1), destination port 4352. Payload, 7 bytes ASCII:
  `%2SRCH\r` = `25 32 53 52 43 48 0D`
- **Reply:** UDP from the device to the sender on port 4352. **Bind local UDP 4352** to receive it. Payload: `%2ACKN=xx:xx:xx:xx:xx:xx\r` (MAC with colons). Source IP of the packet = device IP. Listen for 30 s, since replies are randomly delayed 0-10 s. UDP, so packets can be lost: repeat once if needed.
- **Fields:** MAC (payload), IP (packet source). Name, make, model and firmware need follow-up TCP queries (below).
- **Optional follow-up (TCP 4352, read-only queries):** `%1NAME ?\r` -> `%1NAME=<name>` (max 64 chars UTF-8); `%1INF1 ?\r` -> manufacturer; `%1INF2 ?\r` -> product name; `%1INFO ?\r` -> other info; `%1CLSS ?\r` -> `1`/`2`; `%2SVER ?\r` -> software version; `%2RLMP ?\r` -> lamp/model numbers. Errors: `ERR3` unavailable, `ERR4` projector failure. These control-channel queries **can require the PJLink password** (the TCP greeting says whether auth is on). If auth is required and we have no password, show "PJLink password set" and stop.
- **Also in spec:** `%2LKUP=<mac>\r` link-up notification on UDP 4352, but it is only sent to IP addresses registered in the projector in advance, so we won't receive it by just listening.
- **Safe?** Yes. SRCH is a query with no parameters and changes nothing. Note: "the search response may also be unavailable immediately after the projector starts".

## 2. Art-Net ArtPoll: confirmed

- **Source:** Artistic Licence, Art-Net specification, https://art-net.org.uk/downloads/art-net.pdf (linked from https://art-net.org.uk/art-net-introduction-and-terminology/).
- **Key quotes:** "All communication is UDP." / "Art-Net uses only one port of 0x1936." / "A Controller broadcasts an ArtPoll packet to IP address 2.255.255.255" / "Art-Net packets should not be broadcast to the Limited Broadcast address of 255.255.255.255." / "Consumers of ArtPoll shall accept as valid a packet of length 14 bytes or larger".
- **Who:** DMX/sACN nodes, lighting gateways, LED wall processors, some media servers.
- **Mode:** active.
- **Send:** UDP to the adapter's **directed** subnet broadcast (spec defaults are 2.255.255.255 and 10.255.255.255, but on our cabled network use the adapter's own subnet broadcast). Destination port 6454. Nodes reply to UDP 6454, so **bind local 6454**. Another Art-Net program on the PC may already hold that port, so handle "port in use" with a plain message. Recommended 14-byte ArtPoll:
  ```
  41 72 74 2D 4E 65 74 00   "Art-Net\0"     ID
  00 20                     OpCode 0x2000 (OpPoll), little-endian
  00 0E                     ProtVer 14, high byte first
  00                        Flags = 0x00
  00                        DiagPriority
  ```
  (The full packet is 22 bytes: + TargetPortAddressTop/Bottom (2+2, high byte first), EstaMan (2), Oem (2). Zero-fill or omit; the spec says missing fields count as zero.)
- **ArtPollReply (OpCode 0x2100, at least 207 bytes), 0-based offsets:** 10-13 IP address; 14-15 port (0x1936, low byte first); 16-17 VersInfo H/L = **firmware**; 20-21 Oem Hi/Lo; 24-25 EstaMan Lo/Hi = **manufacturer code**; 26-43 PortName (short name, 18 bytes, null-terminated); 44-107 LongName (64) = **device name**; 108-171 NodeReport (64, status text "#xxxx [yyyy] text"); 201-206 **MAC** (hi byte first; zero if unknown); 207-210 BindIp (root device of a modular product, may be past the 207-byte minimum). A node with many ports sends several replies, so de-duplicate by IP+BindIndex.
- **Safe?** Read-only, but the Flags byte tells the node how to behave towards this controller: bit 1 = "Send ArtPollReply whenever Node conditions change", bit 2 = "Send me diagnostics messages", bit 4 = 0 "Enable VLC transmission", bit 5 = "Enable Targeted Mode". **Send Flags = 0x00**: no unsolicited replies, no diagnostics, no targeted mode, and VLC left at its default (enabled). Nothing is written to the node's configuration. Never send any other Art-Net opcode (ArtAddress, ArtIpProg, ArtDmx etc. *do* change things).

## 3. SAP / AES67 stream announcements: confirmed (passive)

- **Sources:** RFC 2974, https://www.rfc-editor.org/rfc/rfc2974.html: "SAP announcements MUST be sent on port 9875"; the address is "the highest multicast address in the relevant administrative scope zone" (for the 239.255.0.0/16 local scope that is 239.255.255.255). Audinate, https://support.getdante.com/hc/en-gb/articles/5508285534239-Which-network-ports-does-Dante-use: "239.255.255.255, port 9875: SAP (AES67 discovery)". Shure, https://content-files.shure.com/FileRepository/common-ip-ports-v2.pdf: "9875 UDP SAP 239.255.255.255 SAP for AES67".
- **Who:** AES67 senders: Dante devices with AES67 enabled, Ravenna, Q-SYS AES67, Shure Dante cards etc.
- **Mode:** passive. Join 239.255.255.255 on the adapter, bind UDP 9875.
- **Format (RFC 2974 s. 6):** byte 0 = flags (V=1 in top 3 bits, A = IPv6 source, T = 1 deletion / 0 announce, E encrypted, C zlib-compressed); byte 1 = auth length (32-bit words); bytes 2-3 = msg id hash; then originating source (4 bytes IPv4 / 16 bytes IPv6); then auth data; then an optional null-terminated MIME type (`application/sdp`); then the SDP text. Skip packets with E=1; inflate if C=1.
- **Useful SDP lines:** `s=` session (stream) name, `o=` origin (contains the sender's unicast IP), `c=` stream multicast address, `i=` info. Shows *streams* (and therefore the sending device IP), not model/MAC.
- **Safe?** Passive, sends nothing.

## 4. AMX Device Discovery beacon (DDP): partial

- **Sources:**
  - AMX AV/IT Administrators Guide (Rev 1.0, 2014), Appendix A: "9131 UDP DDP Device Discovery Protocol … Discovery of third party devices using multicast beaconing on 239.255.250.250". (Copy hosted by a reseller: https://dextra.com.mx/img/files/PRODUCTOS/AMX/MXR-1001-SL/AMX.AVIT.Administrators.Guide.pdf; Harman's KB page https://harman.helpjuice.com/en_US/dynamic-device-discovery-ddd refers to a DDD white paper that isn't attached.)
  - Optoma projector manual, "AMX Device Discovery Commands" (https://www.manualslib.com/manual/550957/Optoma-Eh501.html?page=80; same in EH500/EH505/EH415e manuals): "DP : 239.255.250.250", "Port No. : 9131", "Each UDP broadcast information as below are updated around 40 seconds". Fields: Device-UUID = "MAC address (Hex value without ':' separator)", Device-SDKClass = "The Duet DeviceSdk class name" (e.g. VideoProjector), Device-Make, Device-Model, Config-URL, Revision ("major.minor.micro").
- **Who:** devices that want AMX controllers to find them: many projectors and displays (Optoma, Epson, NEC, Panasonic etc. with "AMX Device Discovery" settings), some AMX gear.
- **Mode:** passive. Join 239.255.250.250, bind UDP 9131.
- **Format:** field names are confirmed; the exact framing is **not** in an official source. It is commonly seen as ASCII `AMXB<-UUID=…><-SDKClass=…><-Make=…><-Model=…><-Revision=…><Config-URL=…>` (unverified). Parse tolerantly: match `<-?Key=Value>` pairs anywhere in the datagram, and show the raw text if nothing matches.
- **Fields:** Make, Model (model), UUID (= MAC, 12 hex digits), Revision (firmware), Config-URL (web page), packet source IP.
- **Safe?** Passive.

## 5. AMX NetLinx ICSP blink and NDP: partial (presence only)

- **Source:** AMX AV/IT Administrators Guide (above), "Communication Protocols and Network Impact": "The NetLinx Central Controller generates a UDP broadcast message, with source and destination port 1319, every five seconds." / "If NDP Broadcast is enabled, the Central Controller periodically transmits a multicast on 239.255.250.251 with source and destination port 1319."
- **Use:** passive. A UDP broadcast from source port 1319 to port 1319 means "AMX NetLinx master at this IP". The payload format is not documented, so record the IP only. AMX's own tool (NetLinx Studio "Zero-Config") is the supported way to get details.

## 6. Q-SYS Discovery Protocol (QDP): partial

- **Sources:** https://help.qsys.com/Content/Networking/Interfaces_Services.htm: "Q-SYS Discovery Protocol (QDP): Multicast UDP 224.0.23.175 on Ports 2467-2470 and Unicast on Port 6504"; mDNS 224.0.0.251:5353 (LAN A). https://help.qsys.com/Content/Security/Scope_Protocols.htm: "an unencrypted, multicast UDP announcement containing basic device information" / "one multicast QDP packet is sent every second from Q-SYS peripherals and the PC running Q-SYS Designer Software" / "The content of the packets is benign in nature".
- **Mode:** passive. Join 224.0.23.175, bind UDP 2467 (and ideally 2468-2470).
- **Format:** not published by QSC. In practice it is XML text (e.g. `<QDP><device>…<name>…</name><type>…</type>…`), but that is unverified. Recommendation: record the source IP, and if the payload starts with `<`, extract `name`/`type`/`part_number` elements best-effort and label them "reported by device".
- **Safe?** Passive.

## 7. Shure SLP: partial

- **Sources:** Shure "Shure Device IP Ports and Protocols", https://content-files.shure.com/FileRepository/common-ip-ports-v2.pdf: "8427 UDP Multicast SLP 239.255.254.253 Required for service discovery" (AXT/ULXD/AD/SBC group); "5353 udp mDNS Required for device discovery" (MXA310/710/910, ANI, P300, MXN5W-C, MXCW). Shure KB https://service.shure.com/articles/en_US/Knowledge/suu-devices-do-not-display: "blocking discovery traffic on multicast group 239.255.254.253:8427". SLPv2 framing: RFC 2608, https://datatracker.ietf.org/doc/html/rfc2608, section 8 (Version 8 bits = 2, Function-ID 8, Length 16, flags O/F/R + Next Ext Offset 24, XID 16, Lang Tag Length 16, Lang Tag). Function IDs: SrvRqst 1, SrvRply 2, SrvReg 3, DAAdvert 8, SrvTypeRqst 9, SrvTypeRply 10, SAAdvert 11.
- **Note:** Shure uses a non-standard group/port (RFC 2608's standard is 239.255.255.253:427). Shure doesn't publish its service type names.
- **Mode:** passive. Join 239.255.254.253, bind UDP 8427, parse the SLPv2 header and show any URL strings (`service:…`) seen. An active SrvTypeRqst (function 9, read-only per RFC) is plausible but not documented by Shure, so try it only on Simon's hardware pass.
- Shure Dante cards also use SSDP (239.255.255.250:1900) and mDNS, which are already planned.

## 8. Sennheiser SSC over DNS-SD: confirmed (service type)

- **Source:** Sennheiser EW-DX Sound Control Protocol (11/2023), https://www.sennheiser.com/globalassets/digizuite/45056-en-ew-dx_sound_control_protocol_11_2023_en.pdf, s. 6.2: "Networked devices implement DNS-SD (Apple Bonjour) as discovery protocol." / servers "MUST all publish a DNS-SD service under "_ssc._udp"" / "MUST publish another DNS-SD service under "_ssc._tcp"" (if TCP is supported) / "The DNS-SD service instance name must be identical to the device name accessible as /device/name." / "The default port number is 45" ("SSC Clients SHOULD NOT rely on default ports"). TXT keys: `txtvers` ("1"), `version` (SSC version).
- **Fields from mDNS:** instance name = device name; SRV gives host and port; A record gives IP.
- **Optional read-only follow-up (JSON over the SRV port):** getters send `null`, e.g. `{"device":{"identity":{"product":null}}}`. Read-only addresses: `/device/identity/product`, `/device/identity/version`, `/device/identity/vendor`, `/device/identity/serial`, `/device/network/ether/macs`. **Never send a value other than `null`** (`/device/name` is read/write).

## 9. Dante: mDNS (see table above)

- Audinate confirms mDNS/DNS-SD on 224.0.0.251:5353 for "discovery and enumeration of other Dante devices" (https://www.getdante.com/wp-content/uploads/2026/07/Information-for-Network-Admins-v7.1.pdf) and documents only the DDM types `_dante-ddm-d._udp` / `_dante-ddm-c._tcp` (https://dev.audinate.com/GA/ddm/technical-notes/network-admin/pdf/latest). The `_netaudio-*` names are **not** in any Audinate document found. Other Dante multicast traffic you may see passively: 224.0.0.230-233 ports 8700-8708 (control and monitoring), 239.255.255.255:9875 (SAP), PTP 224.0.1.129:319/320.

## Dropped or not confirmed: reasons

- **Crestron autodiscovery (UDP 41794):** Crestron docs list 41794 only as "Proprietary Crestron control communications" (https://docs.crestron.com/en-us/4579/Content/Topics/Plan-a-Network.htm). The 4-Series security guide (https://www.crestron.com/getmedia/092281be-65d9-40e8-983b-926a174c007a/mg_sr_4-series-control-systems) says auto discovery lets devices "be detected, report basic information, and do some basic configuration remotely" and "is not protected by any type of authentication". There is no published format, and the same channel can configure devices, so **don't send anything**. Crestron AirMedia uses mDNS (5353) and UDP 1047-1049 / TCP 31865 for discovery (https://docs.crestron.com/en-us/7693/Content/Topics/AirMedia-Device-Considerations.htm), but no format is published.
- **Biamp Tesira:** "UDP Broadcast for Auto Discovery" on port 61451, UDP 12003 and multicast 224.0.0.252-254, all "Proprietary" (https://support.biamp.com/Tesira/Control/Tesira_network_ports_and_protocols, https://support.biamp.com/Tesira/Control/Tesira_device_discovery_methods). mDNS is enabled by default, so the planned mDNS browse covers Tesira hostnames.
- **Extron:** "4502 UDP Inbound Discovery Broadcast network discovery" (https://media.extron.com/public/download/files/userman/68-2961-01_G_CtrlSystNetworkPortsnLicenses.pdf). Payload not published.
- **Allen & Heath:** AHNet discovery is a UDP broadcast on "Port 51320 at a rate of 1 message / second", with unicast replies (https://www.allen-heath.com/content/uploads/2023/11/AH-dLive-for-IT-managers.pdf). The broadcast comes from the software, not the mixers, and the payload isn't published.
- **Bose Professional ControlSpace:** "Scan/Discovery 9010" (https://assets.boseprofessional.com/m/4998082f60dfee56/original/ControlSpace-Serial-Protocol-v5-13.pdf). No format.
- **HiQnet (UDP/TCP 3804):** Harman KB: "Port 3804 is used to send HiQnet commands" (https://harman.helpjuice.com/general-harman-inquiries/ethernet-tcp-port-used-by-hiqnet-devices). The third-party doc (https://harman.helpjuice.com/en_US/hiqnet-third-party-programmer-documentation) describes Disco Info broadcast ("A Device will announce its arrival by transmitting 5 Disco Info(I) messages") and the header, but the payload table could not be extracted. Legacy platform, so not worth it for Phase 3.
- **Ubiquiti (10001):** "Device Discovery relies on UDP/TCP port 10001" (https://help.uisp.com/hc/en-us/articles/22591202620823-EdgeRouter-Ubiquiti-Device-Discovery). The packet format is only in third-party reverse engineering.
- **Netgear NSDP:** no official Netgear documentation found. The protocol also carries configuration writes. Netgear AV switches support LLDP/SNMP instead.
- **Kramer:** discovery is via the Windows "LAN Configurator", which "discover[s] and configure[s] Kramer's control hardware" (https://www.manula.com/manuals/kramer/k-touch/1/en/topic/kramer-lan-configurator-for-kramer-product-discovery-on-lan). No protocol published.
- **Lightware:** Open API doc covers TCP 6107 (LW3) / 10001 (LW2) control only; no discovery protocol (https://lightware.com/pub/media/lightware/filedownloader/file/Lightware_s_Open_API_Environment_v2.pdf).
- **Atlona:** API docs cover Telnet 23 queries only (`IPCFG`, `Type`, `Version`); no discovery (https://ts.atlona.com/pdf/AT-UHD-CAT-XX_API.pdf).
- **Barco ClickShare:** uses "mDNS/SSDP", UDP 5353 and 1900, plus Wi-Fi/ultrasound/Bluetooth beacons; no service names published (https://www.barco.com/en/support/knowledge-base/which-ports-does-the-clickshare-button-and-base-unit-use, https://www.barco.com/en/support/knowledge-base/order-and-precedence-of-clickshare-app-discovery). Covered by the planned mDNS/SSDP.
- **Yamaha (RIVAGE/CL/QL):** the network guide uses manual IP addressing only; no discovery protocol documented.
- **NDI:** mDNS on 5353 confirmed (https://docs.ndi.video/docs/white-paper/discovery-and-registration/mdns), but the service type name is not on the pages read.
- **Not researched (layer 2, needs a raw-packet driver on Windows):** LLDP (IEEE 802.1AB) and IEEE 1722.1 AVDECC (Q-SYS/Biamp AVB). Out of scope unless we add Npcap.

## Implementation notes

- Bind conflicts: PJLink (4352) and Art-Net (6454) need fixed local ports. Use `SO_REUSEADDR`; if the bind still fails, say which program-type is likely holding it ("Another lighting or projector tool may be running").
- Multicast joins (9131, 2467, 9875, 8427) must be on the selected adapter's interface address, not the default route.
- Active probes: send each once, maybe one retry, and only on the adapter the user selected. PJLink: listen 30 s. Art-Net: replies within ~3 s.
- Every active probe above is a query only. Never send PJLink control commands, Art-Net opcodes other than ArtPoll, SSC values other than `null`, or anything to Crestron 41794.
