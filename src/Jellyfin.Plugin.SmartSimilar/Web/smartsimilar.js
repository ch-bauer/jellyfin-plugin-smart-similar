/* Smart Similar plugin - injected into index.html by the File Transformation plugin. */
(function () {
    'use strict';

    var ITEM_ATTR = 'data-smartsimilar-item';
    var ACTIVE_ATTR = 'data-smartsimilar-active';
    var CACHE_TTL_MS = 30000;
    var cache = new Map(); // itemId -> { time, promise }
    var observerTimer = null;

    function log() {
        try {
            console.debug.apply(console, ['[SmartSimilar]'].concat([].slice.call(arguments)));
        } catch (e) { /* ignore */ }
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text == null ? '' : String(text);
        return div.innerHTML;
    }

    function getVisibleDetailPage() {
        var pages = document.querySelectorAll('.itemDetailPage:not(.hide)');
        return pages.length ? pages[pages.length - 1] : null;
    }

    function getItemIdFromUrl() {
        var raw = (window.location.hash || '').replace(/^#!?/, '');
        var queryIndex = raw.indexOf('?');
        var search = queryIndex >= 0
            ? raw.substring(queryIndex + 1)
            : window.location.search.replace(/^\?/, '');

        try {
            return new URLSearchParams(search).get('id');
        } catch (e) {
            return null;
        }
    }

    /**
     * Asks the plugin for the ranked similar item ids, then loads the items
     * through the standard items API (so permissions, user data and image tags
     * are respected) and restores the ranking order. Kicked off as early as
     * possible (on the navigation event itself) and cached briefly, so the
     * section can render together with the rest of the page.
     */
    function fetchData(itemId) {
        var now = Date.now();
        var entry = cache.get(itemId);
        if (entry && (now - entry.time) < CACHE_TTL_MS) {
            return entry.promise;
        }

        var apiClient = window.ApiClient;
        var userId = apiClient.getCurrentUserId();
        var url = apiClient.getUrl('SmartSimilar/Items', { itemId: itemId, userId: userId });

        var promise = apiClient.getJSON(url).then(function (response) {
            var active = response.Active !== undefined ? response.Active : response.active;
            var ids = response.ItemIds || response.itemIds || [];

            if (!active || !ids.length) {
                return { active: !!active, items: [] };
            }

            return apiClient.getItems(userId, {
                Ids: ids.join(','),
                Fields: 'PrimaryImageAspectRatio,ProductionYear',
                ImageTypeLimit: 1,
                EnableImageTypes: 'Primary'
            }).then(function (result) {
                var byId = new Map();
                var fetched = (result && result.Items) || [];
                for (var i = 0; i < fetched.length; i++) {
                    byId.set(fetched[i].Id, fetched[i]);
                }

                // /Items?Ids= does not guarantee order; restore the ranking.
                var items = [];
                for (var j = 0; j < ids.length; j++) {
                    var item = byId.get(ids[j]);
                    if (item) {
                        items.push(item);
                    }
                }

                return { active: true, items: items };
            });
        });

        promise.catch(function () {
            cache.delete(itemId);
        });

        cache.set(itemId, { time: now, promise: promise });
        if (cache.size > 30) {
            cache.delete(cache.keys().next().value);
        }

        return promise;
    }

    function removeSections(scope) {
        var sections = scope.querySelectorAll('[data-smartsimilar-section]');
        for (var i = 0; i < sections.length; i++) {
            sections[i].parentElement.removeChild(sections[i]);
        }
    }

    function deactivate(page) {
        page.removeAttribute(ACTIVE_ATTR);
        removeSections(page);
    }

    function check() {
        var apiClient = window.ApiClient;
        if (!apiClient) {
            return;
        }

        var itemId = getItemIdFromUrl();
        if (!itemId) {
            return;
        }

        // Start (or reuse) the data fetch right away, even if the target page
        // hasn't been shown yet - by the time it appears the data is ready.
        var dataPromise = fetchData(itemId);

        var page = getVisibleDetailPage();
        if (!page || page.getAttribute(ITEM_ATTR) === itemId) {
            return;
        }

        page.setAttribute(ITEM_ATTR, itemId);
        removeSections(page);

        // Hide the native "More Like This" section optimistically: it starts out
        // hidden anyway and only appears after its own fetch, so claiming it
        // before our data arrives prevents any flicker. If the plugin has
        // nothing for this item, the attribute is removed and the native
        // section behaves as if the plugin wasn't there.
        page.setAttribute(ACTIVE_ATTR, '');

        dataPromise.then(function (data) {
            // Bail if the user navigated elsewhere in the meantime.
            if (page.getAttribute(ITEM_ATTR) !== itemId) {
                return;
            }

            if (!data.active) {
                deactivate(page);
                return;
            }

            // For supported item types the plugin owns the row - even with zero
            // results the native section stays hidden (its results are exactly
            // what this plugin exists to replace, e.g. collection siblings).
            removeSections(page);
            if (data.items.length) {
                insertSection(page, data.items, apiClient);
            }
        }).catch(function (err) {
            log('failed to render similar section', err);
            if (page.getAttribute(ITEM_ATTR) === itemId) {
                deactivate(page);
            }
        });
    }

    /**
     * jellyfin-web stamps the active layout on the <html> element; mirror the
     * native card behavior per layout (hover overlay on desktop, an
     * always-visible play button on mobile, nothing on TV).
     */
    function getLayout() {
        var classes = document.documentElement.classList;
        if (classes.contains('layout-mobile')) {
            return 'mobile';
        }
        if (classes.contains('layout-tv')) {
            return 'tv';
        }
        if (classes.contains('layout-desktop')) {
            return 'desktop';
        }
        return window.matchMedia && window.matchMedia('(pointer: coarse)').matches ? 'mobile' : 'desktop';
    }

    /**
     * Mobile cards: a single always-visible play button in the bottom right.
     * Exact native cardBuilder markup - deliberately WITHOUT item data
     * attributes: the click handler must resolve the item from the card div
     * (which carries data-mediatype etc.); a button-level data-id would make
     * it build an incomplete item that playback silently rejects.
     */
    function buildMobilePlayButton() {
        return '<button is="paper-icon-button-light" class="cardOverlayButton cardOverlayButton-br itemAction"'
            + ' data-action="play" title="Play">'
            + '<span class="material-icons cardOverlayButtonIcon play_arrow" aria-hidden="true"></span></button>';
    }

    /**
     * Native jellyfin-web hover overlay (play, watched, favorite, menu buttons).
     * Same markup jellyfin-web injects on mouseenter; visibility/fade is handled
     * by the web client's own .card:hover CSS.
     */
    function buildHoverOverlay(item, serverId) {
        var userData = item.UserData || {};
        var btnClass = 'cardOverlayButton cardOverlayButton-hover itemAction paper-icon-button-light';
        var iconClass = 'material-icons cardOverlayButtonIcon cardOverlayButtonIcon-hover';
        var itemAttrs = ' data-id="' + item.Id + '" data-serverid="' + serverId + '"'
            + ' data-itemtype="' + (item.Type || 'Movie') + '"';

        var html = '';
        html += '<div class="cardOverlayContainer itemAction" data-action="link">';

        html += '<button is="paper-icon-button-light" type="button" data-action="resume" title="Play"'
            + ' class="' + btnClass + ' cardOverlayFab-primary">'
            + '<span class="' + iconClass + ' play_arrow" aria-hidden="true"></span></button>';

        html += '<div class="cardOverlayButton-br flex">';

        html += '<button is="emby-playstatebutton" type="button" data-action="none"' + itemAttrs
            + ' data-played="' + (userData.Played ? 'true' : 'false') + '"'
            + ' class="' + btnClass + ' emby-button">'
            + '<span class="' + iconClass + ' check playstatebutton-icon-'
            + (userData.Played ? 'played' : 'unplayed') + '" aria-hidden="true"></span></button>';

        html += '<button is="emby-ratingbutton" type="button" data-action="none"' + itemAttrs
            + ' data-likes="" data-isfavorite="' + (userData.IsFavorite ? 'true' : 'false') + '"'
            + ' class="' + btnClass + ' emby-button">'
            + '<span class="' + iconClass + ' favorite'
            + (userData.IsFavorite ? ' ratingbutton-icon-withrating' : '') + '" aria-hidden="true"></span></button>';

        html += '<button is="paper-icon-button-light" type="button" data-action="menu" title="More"'
            + ' class="' + btnClass + '">'
            + '<span class="' + iconClass + ' more_vert" aria-hidden="true"></span></button>';

        html += '</div></div>';
        return html;
    }

    function buildCard(item, index, apiClient) {
        var serverId = item.ServerId || apiClient.serverId();
        var itemType = item.Type || 'Movie';
        var isFolder = itemType === 'Series';
        var href = '#/details?id=' + item.Id + '&serverId=' + serverId;
        var name = escapeHtml(item.Name || '');

        var imgUrl = null;
        if (item.ImageTags && item.ImageTags.Primary) {
            var imgOptions = { type: 'Primary', maxWidth: 400, tag: item.ImageTags.Primary };
            imgUrl = typeof apiClient.getScaledImageUrl === 'function'
                ? apiClient.getScaledImageUrl(item.Id, imgOptions)
                : apiClient.getUrl('Items/' + item.Id + '/Images/Primary', { maxWidth: 400, tag: item.ImageTags.Primary, quality: 90 });
        }

        var html = '';
        html += '<div class="card overflowPortraitCard card-hoverable card-withuserdata" data-index="' + index + '"'
            + ' data-isfolder="' + (isFolder ? 'true' : 'false') + '"'
            + ' data-serverid="' + serverId + '" data-id="' + item.Id + '"'
            + ' data-type="' + itemType + '"'
            + (isFolder ? '' : ' data-mediatype="Video"') + '>';
        html += '<div class="cardBox cardBox-bottompadded">';
        html += '<div class="cardScalable">';
        html += '<div class="cardPadder cardPadder-overflowPortrait"></div>';

        if (imgUrl) {
            html += '<a href="' + href + '" class="cardImageContainer coveredImage cardContent itemAction"'
                + ' data-action="link" aria-label="' + name + '"'
                + ' style="background-image:url(\'' + imgUrl + '\')"></a>';
        } else {
            html += '<a href="' + href + '" class="cardImageContainer coveredImage cardContent itemAction'
                + ' defaultCardBackground defaultCardBackground' + ((index % 4) + 1) + '"'
                + ' data-action="link" aria-label="' + name + '">'
                + '<div class="cardText cardDefaultText">' + name + '</div></a>';
        }

        if (item.UserData && item.UserData.Played) {
            html += '<div class="cardIndicators"><div class="playedIndicator indicator">'
                + '<span class="material-icons indicatorIcon check" aria-hidden="true"></span></div></div>';
        }

        var layout = getLayout();
        if (layout === 'desktop') {
            html += buildHoverOverlay(item, serverId);
        } else if (layout === 'mobile') {
            html += buildMobilePlayButton();
        }
        html += '</div>';
        html += '<div class="cardText cardTextCentered cardText-first"><bdi>'
            + '<a href="' + href + '" data-id="' + item.Id + '" data-serverid="' + serverId + '"'
            + ' data-type="' + itemType + '"'
            + (isFolder ? ' data-isfolder="true"' : ' data-mediatype="Video" data-isfolder="false"')
            + ' class="itemAction textActionButton" title="' + name + '" data-action="link">' + name + '</a>'
            + '</bdi></div>';
        html += '<div class="cardText cardTextCentered cardText-secondary"><bdi>'
            + (item.ProductionYear ? escapeHtml(item.ProductionYear) : '&nbsp;') + '</bdi></div>';
        html += '</div></div>';
        return html;
    }

    /**
     * The localized section title ("More Like This" / "Ähnliches" / ...) is
     * present in the native section's static markup even while that section is
     * hidden, so it can be reused without ever showing the native section.
     */
    function getSectionTitle(page) {
        var nativeTitle = page.querySelector('#similarCollapsible .sectionTitle');
        var text = nativeTitle && nativeTitle.textContent ? nativeTitle.textContent.trim() : '';
        return text || 'More Like This';
    }

    function insertSection(page, items, apiClient) {
        var content = page.querySelector('.detailPageContent');
        if (!content) {
            log('detailPageContent not found, skipping');
            return;
        }

        var section = document.createElement('div');
        section.className = 'verticalSection detailVerticalSection smartSimilarSection';
        section.setAttribute('data-smartsimilar-section', 'true');

        var html = '';
        html += '<h2 class="sectionTitle sectionTitle-cards padded-right">'
            + escapeHtml(getSectionTitle(page)) + '</h2>';
        html += '<div is="emby-scroller" class="padded-top-focusscale padded-bottom-focusscale no-padding"'
            + ' data-mousewheel="false" data-centerfocus="card">';
        html += '<div is="emby-itemscontainer" class="focuscontainer-x itemsContainer scrollSlider">';

        for (var i = 0; i < items.length; i++) {
            html += buildCard(items[i], i, apiClient);
        }

        html += '</div></div>';
        section.innerHTML = html;

        // Take the native section's exact place; fall back to the cast section
        // (native puts "More Like This" right above it) or the top.
        var nativeSection = page.querySelector('#similarCollapsible');
        var cast = page.querySelector('#castCollapsible') || page.querySelector('.peopleSection');
        if (nativeSection && nativeSection.parentElement) {
            nativeSection.parentElement.insertBefore(section, nativeSection);
        } else if (cast && cast.parentElement) {
            cast.parentElement.insertBefore(section, cast);
        } else {
            content.insertBefore(section, content.firstChild);
        }

        log('inserted similar section with ' + items.length + ' items');
    }

    // Navigation events run immediately - the fetch must start as early as possible.
    document.addEventListener('viewshow', check, true);
    window.addEventListener('hashchange', check);
    window.addEventListener('popstate', check);

    // The MutationObserver is only a safety net (e.g. initial page load); debounced.
    var observer = new MutationObserver(function () {
        if (observerTimer) {
            clearTimeout(observerTimer);
        }
        observerTimer = setTimeout(check, 100);
    });

    function start() {
        if (!document.body) {
            setTimeout(start, 100);
            return;
        }
        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['class']
        });
        check();
        log('initialized');
    }

    start();
})();
