export default function (view) {
    var pluginId = '7f1c2a54-9b3d-4c6e-8a10-2c9e5d4b7f31';

    function list(data, name) {
        if (!data) {
            return [];
        }
        var pascal = name.charAt(0).toUpperCase() + name.slice(1);
        return data[name] || data[pascal] || [];
    }

    function noteOf(data) {
        return (data && (data.note || data.Note)) || '';
    }

    function req(method, path, body) {
        var client = window.ApiClient;
        if (!client) {
            return Promise.reject(new Error('ApiClient is not available on this page.'));
        }
        var url = client.getUrl('RogueStreams' + path);
        if (method === 'GET') {
            return client.getJSON(url);
        }
        return client.ajax({
            type: method,
            url: url,
            data: JSON.stringify(body || {}),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    function bytes(n) {
        if (n == null) {
            return '';
        }
        if (n < 1048576) {
            return Math.round(n / 1024) + ' KB';
        }
        if (n < 1073741824) {
            return (n / 1048576).toFixed(1) + ' MB';
        }
        return (n / 1073741824).toFixed(2) + ' GB';
    }

    function who(stream) {
        var bits = [];
        list(stream, 'viewers').forEach(function (v) {
            bits.push((v.user || v.User || '?') + ' @ ' + (v.device || v.Device || '?'));
        });
        list(stream, 'recordings').forEach(function (r) {
            bits.push('rec: ' + r);
        });
        if (!bits.length) {
            return '<span style="color:#ff9b8a;">nobody</span>';
        }
        return '<span style="color:#7bd88f;">' + bits.join('<br>') + '</span>';
    }

    function q(id) {
        return view.querySelector('#' + id);
    }

    function esc(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function tunerHtml(row) {
        var tuner = row && (row.tuner || row.Tuner);
        var streamType = (row && (row.streamType || row.StreamType)) || '';
        if (!tuner) {
            return '<span style="opacity:.55;">' + (streamType ? esc(streamType) : 'unknown') + '</span>';
        }

        var kind = tuner.kind || tuner.Kind || tuner.type || tuner.Type || 'Tuner';
        var name = tuner.name || tuner.Name || '';
        var url = tuner.url || tuner.Url || '';
        var configured = tuner.configured != null ? tuner.configured : tuner.Configured;
        var bits = [];
        if (name && name !== kind) {
            bits.push(esc(name));
        }
        if (url) {
            bits.push(esc(url));
        }
        if (streamType) {
            bits.push(esc(streamType));
        }

        var html = '<div>' + esc(kind) + '</div>';
        if (bits.length) {
            html += '<div style="opacity:.6; font-size:.85em; word-break:break-word;">' + bits.join(' &middot; ') + '</div>';
        }
        if (configured === false) {
            html += '<div style="color:#ff9b8a; font-size:.8em;">not in Live TV config</div>';
        }
        return html;
    }

    function camel(name) {
        return name.charAt(0).toLowerCase() + name.slice(1);
    }

    function configGet(config, name) {
        if (!config) {
            return '';
        }
        var value = config[name];
        if (value == null) {
            value = config[camel(name)];
        }
        return value == null ? '' : value;
    }

    function configSet(config, name, value) {
        var key = Object.prototype.hasOwnProperty.call(config, camel(name)) ? camel(name) : name;
        config[key] = value;
    }

    function setSettingsMessage(text, isError) {
        var el = q('rsSettingsMsg');
        if (!el) {
            return;
        }
        el.textContent = text || '';
        el.style.color = isError ? '#ff9b8a' : '#7bd88f';
    }

    function loadSettings() {
        var client = window.ApiClient;
        if (!client) {
            return;
        }
        client.getPluginConfiguration(pluginId).then(function (config) {
            var input = q('rsTunerUrl');
            if (input) {
                input.value = configGet(config, 'TunerUrl');
            }
            setSettingsMessage('', false);
        }).catch(function (e) {
            setSettingsMessage('Could not load settings: ' + (e.message || e), true);
        });
    }

    function saveSettings() {
        var client = window.ApiClient;
        if (!client) {
            setSettingsMessage('ApiClient is not available on this page.', true);
            return;
        }

        var value = q('rsTunerUrl').value.trim();
        setSettingsMessage('Saving...', false);
        client.getPluginConfiguration(pluginId).then(function (config) {
            configSet(config, 'TunerUrl', value);
            return client.updatePluginConfiguration(pluginId, config);
        }).then(function (result) {
            if (window.Dashboard && window.Dashboard.processPluginConfigurationUpdateResult) {
                window.Dashboard.processPluginConfigurationUpdateResult(result);
            }
            setSettingsMessage(value ? 'Saved. Tuner leases will use this URL.' : 'Saved. Tuner lease lookup is disabled.', false);
            refresh();
        }).catch(function (e) {
            setSettingsMessage('Could not save: ' + (e.message || e), true);
        });
    }

    function testTuner() {
        var value = q('rsTunerUrl').value.trim();
        if (!value) {
            setSettingsMessage('Enter or auto-detect an HDHomeRun URL first.', true);
            return;
        }

        setSettingsMessage('Testing ' + value + '...', false);
        req('POST', '/TestTuner', { url: value }).then(function (result) {
            var reachable = result.reachable != null ? result.reachable : result.Reachable;
            var message = result.message || result.Message || '';
            if (!reachable) {
                setSettingsMessage(message || 'Tuner did not respond.', true);
                return;
            }

            var bits = [];
            var friendly = result.friendlyName || result.FriendlyName;
            var model = result.modelNumber || result.ModelNumber;
            var count = result.tunerCount != null ? result.tunerCount : result.TunerCount;
            if (friendly) {
                bits.push(friendly);
            }
            if (model) {
                bits.push(model);
            }
            if (count) {
                bits.push(count + ' tuner(s)');
            }
            bits.push(message || 'Tuner responded.');
            setSettingsMessage(bits.join(' \u00b7 '), false);
        }).catch(function (e) {
            setSettingsMessage('Test failed: ' + (e.message || e), true);
        });
    }

    function useDetected(tuner) {
        var url = tuner.url || tuner.Url || '';
        q('rsTunerUrl').value = url;
        setSettingsMessage('Selected ' + url + '. Click Save tuner setting to keep it.', false);
    }

    function renderDetected(tuners) {
        var box = q('rsDetectResults');
        box.innerHTML = '';
        tuners.forEach(function (tuner) {
            var url = tuner.url || tuner.Url || '';
            var name = tuner.friendlyName || tuner.FriendlyName || 'HDHomeRun';
            var device = tuner.deviceId || tuner.DeviceId || '';
            var count = tuner.tunerCount != null ? tuner.tunerCount : tuner.TunerCount;

            var row = document.createElement('div');
            row.style.cssText = 'display:flex; gap:.75em; align-items:center; justify-content:space-between; padding:.5em 0; border-top:1px solid rgba(255,255,255,.1);';

            var details = document.createElement('div');
            var title = document.createElement('strong');
            title.textContent = name;
            details.appendChild(title);

            var meta = document.createElement('div');
            meta.style.cssText = 'opacity:.7; font-size:.9em; word-break:break-all;';
            var bits = [url];
            if (device) {
                bits.push('device ' + device);
            }
            if (count) {
                bits.push(count + ' tuner(s)');
            }
            meta.textContent = bits.join(' \u00b7 ');
            details.appendChild(meta);

            var use = document.createElement('button');
            use.setAttribute('is', 'emby-button');
            use.type = 'button';
            use.className = 'raised';
            use.textContent = 'Use';
            use.addEventListener('click', function () {
                useDetected(tuner);
            });

            row.appendChild(details);
            row.appendChild(use);
            box.appendChild(row);
        });
    }

    function detectTuners() {
        var box = q('rsDetectResults');
        box.innerHTML = '';
        setSettingsMessage('Scanning the local network for HDHomeRun devices...', false);
        req('GET', '/DiscoverTuners').then(function (results) {
            var tuners = Array.isArray(results) ? results : ((results && results.items) || []);
            if (!tuners.length) {
                setSettingsMessage('No HDHomeRun found. Check that the tuner is on the same network as Jellyfin, or enter its URL manually.', true);
                return;
            }

            setSettingsMessage('Found ' + tuners.length + ' tuner(s). Click Use, then Save tuner setting.', false);
            renderDetected(tuners);
        }).catch(function (e) {
            setSettingsMessage('Auto-detect failed: ' + (e.message || e), true);
        });
    }

    function render(d) {
        var note = q('rsNote');
        var noteText = noteOf(d);
        if (noteText) {
            note.style.display = 'block';
            note.textContent = noteText;
        } else {
            note.style.display = 'none';
        }
        q('rsError').style.display = 'none';

        var streams = list(d, 'streams');
        var tb = q('rsStreams');
        tb.innerHTML = '';
        if (!streams.length) {
            tb.innerHTML = '<tr><td colspan="6" style="padding:.5em .4em; opacity:.6;">No open live streams in this Jellyfin process.</td></tr>';
        }
        streams.forEach(function (s) {
            var tr = document.createElement('tr');
            tr.style.borderBottom = '1px solid rgba(255,255,255,.07)';
            var name = s.name || s.Name || 'live stream';
            var liveStreamId = s.liveStreamId || s.LiveStreamId || '';
            var consumers = s.consumerCount != null ? s.consumerCount : s.ConsumerCount;
            var path = s.path || s.Path || '';
            var fileBytes = s.fileBytes != null ? s.fileBytes : s.FileBytes;
            var inUse = s.inUse || s.InUse;
            tr.innerHTML =
                '<td style="padding:.4em;">' +
                    '<div>' + name + '</div>' +
                    '<div style="opacity:.5; font-size:.85em; word-break:break-all;">' + liveStreamId + '</div>' +
                '</td>' +
                '<td style="padding:.4em;">' + tunerHtml(s) + '</td>' +
                '<td style="padding:.4em; opacity:.8;">' + consumers + '</td>' +
                '<td style="padding:.4em;">' + who(s) + '</td>' +
                '<td style="padding:.4em; opacity:.7;">' +
                    path +
                    (fileBytes != null ? '<div>' + bytes(fileBytes) + '</div>' : '') +
                '</td>' +
                '<td style="padding:.4em; text-align:right;"></td>';

            var btn = document.createElement('button');
            btn.setAttribute('is', 'emby-button');
            btn.className = 'raised button-cancel block';
            btn.textContent = 'Close';
            btn.addEventListener('click', function () {
                var warn = inUse
                    ? 'This stream looks in use (viewer or recording). Closing it will stop them.\n\nClose anyway?'
                    : 'Close this live stream and release its tuner?';
                if (!confirm(warn)) {
                    return;
                }
                btn.disabled = true;
                req('POST', '/Close', { liveStreamId: liveStreamId }).then(render).catch(function (e) {
                    alert(e.message || e);
                    btn.disabled = false;
                    refresh();
                });
            });
            tr.lastElementChild.appendChild(btn);
            tb.appendChild(tr);
        });

        var tuners = list(d, 'tuners');
        var tn = q('rsTuners');
        tn.innerHTML = '';
        if (!tuners.length) {
            tn.innerHTML = '<tr><td colspan="4" style="padding:.5em .4em; opacity:.6;">No HDHomeRun leases (or tuner URL not set / unreachable).</td></tr>';
        }
        tuners.forEach(function (t) {
            var tr = document.createElement('tr');
            tr.innerHTML =
                '<td style="padding:.3em .4em; opacity:.7;">' + (t.resource || t.Resource || '') + '</td>' +
                '<td style="padding:.3em .4em;">' + (t.channel || t.Channel || '') + '</td>' +
                '<td style="padding:.3em .4em;">' + (t.name || t.Name || '\u2014') + '</td>' +
                '<td style="padding:.3em .4em; opacity:.7;">' + ((t.networkRate || t.NetworkRate || 0) / 1000000).toFixed(2) + ' MB/s</td>';
            tn.appendChild(tr);
        });

        var sessions = list(d, 'sessions');
        var sn = q('rsSessions');
        sn.innerHTML = '';
        if (!sessions.length) {
            sn.innerHTML = '<tr><td style="padding:.4em; opacity:.6;">Nobody is watching live TV.</td></tr>';
        }
        sessions.forEach(function (s) {
            var tr = document.createElement('tr');
            tr.innerHTML =
                '<td style="padding:.3em .4em;">' + (s.user || s.User || '') + '</td>' +
                '<td style="padding:.3em .4em; opacity:.7;">' + (s.device || s.Device || '') + '</td>' +
                '<td style="padding:.3em .4em;">' + (s.channel || s.Channel || s.itemName || s.ItemName || '') + '</td>' +
                '<td style="padding:.3em .4em;">' + tunerHtml(s) + '</td>' +
                '<td style="padding:.3em .4em; opacity:.7;">' + (s.client || s.Client || '') + ' ' + (s.playMethod || s.PlayMethod || '') + '</td>';
            sn.appendChild(tr);
        });

        var recordings = list(d, 'recordings');
        var rn = q('rsRecordings');
        rn.innerHTML = '';
        if (!recordings.length) {
            rn.innerHTML = '<tr><td style="padding:.4em; opacity:.6;">No in-progress recordings.</td></tr>';
        }
        recordings.forEach(function (r) {
            var tr = document.createElement('tr');
            tr.innerHTML =
                '<td style="padding:.3em .4em;">' + (r.name || r.Name || '') + '</td>' +
                '<td style="padding:.3em .4em; opacity:.7;">' + (r.channel || r.Channel || '') + '</td>' +
                '<td style="padding:.3em .4em; opacity:.7;">' + (r.status || r.Status || '') + '</td>';
            rn.appendChild(tr);
        });

        q('rsStamp').textContent =
            'Updated ' + new Date().toLocaleTimeString() + ' \u2014 refreshes every 5s while this page is open.';
    }

    function refresh() {
        req('GET', '/Status').then(render).catch(function (e) {
            var err = q('rsError');
            err.style.display = 'block';
            err.textContent = e.message || String(e);
        });
    }

    var timer;
    view.addEventListener('viewshow', function () {
        q('rsDetectBtn').onclick = detectTuners;
        q('rsTestBtn').onclick = testTuner;
        q('rsSaveBtn').onclick = saveSettings;
        loadSettings();
        refresh();
        if (timer) {
            clearInterval(timer);
        }
        timer = setInterval(refresh, 5000);
    });
    view.addEventListener('viewhide', function () {
        if (timer) {
            clearInterval(timer);
            timer = null;
        }
    });
}
