(function () {
    const searches = new Map();
    const maps = new Map();
    let loaderPromise;

    function loadGoogleMaps(apiKey) {
        if (window.google?.maps?.importLibrary) {
            return Promise.resolve();
        }

        if (loaderPromise) {
            return loaderPromise;
        }

        loaderPromise = new Promise((resolve, reject) => {
            const callbackName = `malievQuoteGoogleMapsReady_${Date.now()}`;
            window[callbackName] = () => {
                delete window[callbackName];
                resolve();
            };

            const script = document.createElement("script");
            script.src = `https://maps.googleapis.com/maps/api/js?key=${encodeURIComponent(apiKey)}&v=weekly&loading=async&callback=${callbackName}`;
            script.async = true;
            script.onerror = () => {
                delete window[callbackName];
                reject(new Error("Google Maps JavaScript API could not be loaded."));
            };
            document.head.appendChild(script);
        });

        return loaderPromise;
    }

    function component(components, type) {
        return components?.find(item => item.types?.includes(type));
    }

    function componentText(components, type) {
        const match = component(components, type);
        return match?.longText || match?.long_name || match?.shortText || match?.short_name || null;
    }

    function firstComponentText(components, types) {
        for (const type of types) {
            const value = componentText(components, type);
            if (value) {
                return value;
            }
        }

        return null;
    }

    function joinParts(parts) {
        return parts.filter(Boolean).join(" ").trim() || null;
    }

    function normalizeSelection(source, place, location, components) {
        const lat = typeof location?.lat === "function" ? location.lat() : location?.lat;
        const lng = typeof location?.lng === "function" ? location.lng() : location?.lng;
        const streetNumber = componentText(components, "street_number");
        const route = componentText(components, "route");
        const premise = firstComponentText(components, ["premise", "subpremise"]);

        return {
            source,
            placeId: place?.id || place?.place_id || null,
            formattedAddress: place?.formattedAddress || place?.formatted_address || null,
            addressLine1: joinParts([premise, streetNumber, route]),
            district: firstComponentText(components, ["sublocality_level_2", "sublocality_level_1", "locality"]),
            city: firstComponentText(components, ["administrative_area_level_2", "locality", "sublocality_level_1"]),
            stateProvince: componentText(components, "administrative_area_level_1"),
            postalCode: componentText(components, "postal_code"),
            latitude: Number.isFinite(lat) ? lat : null,
            longitude: Number.isFinite(lng) ? lng : null
        };
    }

    async function initializeSearch(elementId, dotNetReference, config) {
        const container = document.getElementById(elementId);
        if (!container || !config?.apiKey) {
            return;
        }

        await loadGoogleMaps(config.apiKey);
        const { PlaceAutocompleteElement } = await google.maps.importLibrary("places");
        container.replaceChildren();

        const autocomplete = new PlaceAutocompleteElement({
            includedRegionCodes: config.includedRegionCodes?.length ? config.includedRegionCodes : ["th"]
        });
        autocomplete.placeholder = "Search for your location";
        autocomplete.classList.add("maliev-google-place-autocomplete");
        container.appendChild(autocomplete);

        const handler = async event => {
            const place = event.placePrediction?.toPlace();
            if (!place) {
                return;
            }

            await place.fetchFields({
                fields: ["id", "displayName", "formattedAddress", "location", "addressComponents"]
            });

            await dotNetReference.invokeMethodAsync(
                "NotifyGoogleAddressSelected",
                normalizeSelection("GooglePlace", place, place.location, place.addressComponents));
        };

        autocomplete.addEventListener("gmp-select", handler);
        searches.set(elementId, { autocomplete, handler });
    }

    async function initializeMap(elementId, dotNetReference, config, current) {
        const container = document.getElementById(elementId);
        if (!container || !config?.apiKey) {
            return;
        }

        await loadGoogleMaps(config.apiKey);
        const { Map } = await google.maps.importLibrary("maps");
        const { AdvancedMarkerElement } = await google.maps.importLibrary("marker");
        await google.maps.importLibrary("geocoding");

        const position = {
            lat: Number(current?.latitude) || Number(config.defaultLatitude) || 13.7563,
            lng: Number(current?.longitude) || Number(config.defaultLongitude) || 100.5018
        };

        const map = new Map(container, {
            center: position,
            zoom: Number(config.defaultZoom) || 12,
            mapId: config.mapId || undefined,
            streetViewControl: false,
            mapTypeControl: false,
            fullscreenControl: false
        });

        const marker = new AdvancedMarkerElement({
            map,
            position,
            gmpDraggable: true,
            title: "Selected address"
        });

        const geocoder = new google.maps.Geocoder();
        const publishLocation = async latLng => {
            const literal = typeof latLng.lat === "function"
                ? { lat: latLng.lat(), lng: latLng.lng() }
                : latLng;
            marker.position = literal;

            const response = await geocoder.geocode({ location: literal, region: "th" });
            const result = response.results?.[0];
            if (!result) {
                await dotNetReference.invokeMethodAsync("NotifyGoogleAddressSelected", {
                    source: "GoogleMapPin",
                    latitude: literal.lat,
                    longitude: literal.lng
                });
                return;
            }

            await dotNetReference.invokeMethodAsync(
                "NotifyGoogleAddressSelected",
                normalizeSelection("GoogleMapPin", result, literal, result.address_components));
        };

        marker.addListener("dragend", event => publishLocation(event.latLng));
        map.addListener("click", event => publishLocation(event.latLng));
        maps.set(elementId, { map, marker });
    }

    function disposeSearch(elementId) {
        const state = searches.get(elementId);
        if (state) {
            state.autocomplete.removeEventListener("gmp-select", state.handler);
            searches.delete(elementId);
        }
    }

    function disposeMap(elementId) {
        const state = maps.get(elementId);
        if (state?.marker) {
            state.marker.map = null;
        }
        maps.delete(elementId);
    }

    window.malievQuoteGoogleAddressPicker = {
        initializeSearch,
        initializeMap,
        disposeSearch,
        disposeMap
    };
})();
