// ShareApp - Google Maps Integration
let map;
let markers = [];
let infoWindow;
let userLocation = { lat: 40.7128, lng: -74.0060 }; // Default: New York City
let userMarker;
let items = [];

// Marker icons for different item types
const markerIcons = {
    0: { // For Sale
        url: 'https://maps.google.com/mapfiles/ms/icons/green-dot.png',
        scaledSize: { width: 32, height: 32 }
    },
    1: { // Free
        url: 'https://maps.google.com/mapfiles/ms/icons/blue-dot.png',
        scaledSize: { width: 32, height: 32 }
    },
    2: { // Barter
        url: 'https://maps.google.com/mapfiles/ms/icons/orange-dot.png',
        scaledSize: { width: 32, height: 32 }
    },
    user: {
        url: 'https://maps.google.com/mapfiles/ms/icons/red-dot.png',
        scaledSize: { width: 40, height: 40 }
    }
};

// Initialize map (called by Google Maps API callback)
function initMap() {
    console.log('Initializing map...');

    // Create map
    map = new google.maps.Map(document.getElementById('map'), {
        center: userLocation,
        zoom: 13,
        mapTypeControl: true,
        streetViewControl: true,
        fullscreenControl: true
    });

    infoWindow = new google.maps.InfoWindow();

    // Hide loading overlay
    const loadingOverlay = document.getElementById('mapLoading');
    if (loadingOverlay) {
        loadingOverlay.style.display = 'none';
    }

    // Get user's current location
    getUserLocation();

    // Load categories
    loadCategories();

    // Setup event listeners
    setupEventListeners();
}

// Get user's current location
function getUserLocation() {
    const statusElement = document.getElementById('locationStatus');

    if (navigator.geolocation) {
        navigator.geolocation.getCurrentPosition(
            (position) => {
                userLocation = {
                    lat: position.coords.latitude,
                    lng: position.coords.longitude
                };

                // Update map center
                map.setCenter(userLocation);

                // Add user marker
                if (userMarker) {
                    userMarker.setMap(null);
                }
                userMarker = new google.maps.Marker({
                    position: userLocation,
                    map: map,
                    icon: markerIcons.user,
                    title: 'Your Location',
                    zIndex: 1000
                });

                // Update status
                statusElement.innerHTML = '<i class="fas fa-check-circle text-success"></i> Location found';

                // Load nearby items
                loadNearbyItems();
            },
            (error) => {
                console.error('Geolocation error:', error);
                statusElement.innerHTML = '<i class="fas fa-exclamation-triangle text-warning"></i> Using default location';

                // Still load items with default location
                loadNearbyItems();
            }
        );
    } else {
        statusElement.innerHTML = '<i class="fas fa-times-circle text-danger"></i> Geolocation not supported';
        loadNearbyItems();
    }
}

// Load categories from API
async function loadCategories() {
    try {
        const response = await fetch('/api/ItemsApi/categories');
        if (response.ok) {
            const categories = await response.json();
            const categorySelect = document.getElementById('categoryFilter');

            categories.forEach(category => {
                const option = document.createElement('option');
                option.value = category.id;
                option.textContent = category.name;
                categorySelect.appendChild(option);
            });
        }
    } catch (error) {
        console.error('Error loading categories:', error);
    }
}

// Load nearby items from API
async function loadNearbyItems() {
    try {
        const radius = document.getElementById('radiusSlider').value;
        const categoryId = document.getElementById('categoryFilter').value;
        const minPrice = document.getElementById('minPrice').value;
        const maxPrice = document.getElementById('maxPrice').value;

        // Get selected item types
        const selectedTypes = [];
        document.querySelectorAll('.item-type-filter:checked').forEach(checkbox => {
            selectedTypes.push(checkbox.value);
        });

        // Build query string
        let queryParams = `latitude=${userLocation.lat}&longitude=${userLocation.lng}&radiusMiles=${radius}`;

        if (categoryId) {
            queryParams += `&categoryId=${categoryId}`;
        }
        if (minPrice) {
            queryParams += `&minPrice=${minPrice}`;
        }
        if (maxPrice) {
            queryParams += `&maxPrice=${maxPrice}`;
        }

        const response = await fetch(`/api/ItemsApi/nearby?${queryParams}`);

        if (response.ok) {
            const allItems = await response.json();

            // Filter by item type (client-side)
            items = allItems.filter(item => {
                const itemTypeValue = getItemTypeValue(item.itemType);
                return selectedTypes.includes(itemTypeValue.toString());
            });

            displayMarkers();
            updateItemCount();
        } else {
            console.error('Error fetching items:', response.statusText);
        }
    } catch (error) {
        console.error('Error loading nearby items:', error);
    }
}

// Get numeric item type value
function getItemTypeValue(itemTypeString) {
    switch (itemTypeString) {
        case 'ForSale': return 0;
        case 'Free': return 1;
        case 'Barter': return 2;
        default: return 0;
    }
}

// Display markers on map
function displayMarkers() {
    // Clear existing markers
    markers.forEach(marker => marker.setMap(null));
    markers = [];

    // Add new markers
    items.forEach(item => {
        const itemTypeValue = getItemTypeValue(item.itemType);

        const marker = new google.maps.Marker({
            position: { lat: item.latitude, lng: item.longitude },
            map: map,
            icon: markerIcons[itemTypeValue],
            title: item.title
        });

        // Add click listener for info window
        marker.addListener('click', () => {
            showItemInfoWindow(marker, item);
        });

        markers.push(marker);
    });

    // Fit bounds to show all markers
    if (markers.length > 0) {
        const bounds = new google.maps.LatLngBounds();
        bounds.extend(userLocation);
        markers.forEach(marker => bounds.extend(marker.getPosition()));
        map.fitBounds(bounds);

        // Don't zoom in too much
        const listener = google.maps.event.addListener(map, "idle", function () {
            if (map.getZoom() > 15) map.setZoom(15);
            google.maps.event.removeListener(listener);
        });
    }
}

// Show info window for item
function showItemInfoWindow(marker, item) {
    const priceDisplay = item.itemType === 'ForSale' && item.price
        ? `<p class="mb-1"><strong class="text-success">$${item.price.toFixed(2)}</strong></p>`
        : item.itemType === 'Free'
            ? `<p class="mb-1"><strong class="text-primary">FREE</strong></p>`
            : `<p class="mb-1"><strong class="text-info">BARTER</strong></p>`;

    const content = `
        <div style="max-width: 250px;">
            <img src="${item.imageUrl}" alt="${item.title}" class="img-fluid rounded mb-2" style="max-height: 150px; width: 100%; object-fit: cover;" />
            <h6 class="mb-1">${item.title}</h6>
            <p class="text-muted small mb-1">
                <i class="fas fa-tag"></i> ${item.categoryName}
            </p>
            ${priceDisplay}
            <p class="text-muted small mb-2">
                <i class="fas fa-map-marker-alt"></i> ${item.distance.toFixed(1)} miles away
            </p>
            <a href="/Items/Details/${item.id}" class="btn btn-sm btn-primary w-100">
                <i class="fas fa-eye"></i> View Details
            </a>
        </div>
    `;

    infoWindow.setContent(content);
    infoWindow.open(map, marker);
}

// Update item count display
function updateItemCount() {
    document.getElementById('itemCount').textContent = items.length;
}

// Setup event listeners
function setupEventListeners() {
    // Radius slider
    const radiusSlider = document.getElementById('radiusSlider');
    const radiusValue = document.getElementById('radiusValue');

    radiusSlider.addEventListener('input', (e) => {
        radiusValue.textContent = e.target.value;
    });

    // Apply filters button
    document.getElementById('applyFilters').addEventListener('click', () => {
        loadNearbyItems();
    });

    // Reset filters button
    document.getElementById('resetFilters').addEventListener('click', () => {
        document.getElementById('radiusSlider').value = 10;
        document.getElementById('radiusValue').textContent = '10';
        document.getElementById('categoryFilter').value = '';
        document.getElementById('minPrice').value = '';
        document.getElementById('maxPrice').value = '';

        // Reset item type checkboxes
        document.querySelectorAll('.item-type-filter').forEach(checkbox => {
            checkbox.checked = true;
        });
        document.getElementById('typeAll').checked = true;

        loadNearbyItems();
    });

    // Item type "All" checkbox
    document.getElementById('typeAll').addEventListener('change', (e) => {
        const checked = e.target.checked;
        document.querySelectorAll('.item-type-filter').forEach(checkbox => {
            checkbox.checked = checked;
        });
    });

    // Individual item type checkboxes
    document.querySelectorAll('.item-type-filter').forEach(checkbox => {
        checkbox.addEventListener('change', () => {
            const allChecked = Array.from(document.querySelectorAll('.item-type-filter'))
                .every(cb => cb.checked);
            document.getElementById('typeAll').checked = allChecked;
        });
    });

    // Apply filters on Enter key in price fields
    document.getElementById('minPrice').addEventListener('keypress', (e) => {
        if (e.key === 'Enter') {
            loadNearbyItems();
        }
    });

    document.getElementById('maxPrice').addEventListener('keypress', (e) => {
        if (e.key === 'Enter') {
            loadNearbyItems();
        }
    });
}

// Fallback if Google Maps doesn't load
window.addEventListener('load', () => {
    setTimeout(() => {
        if (!map) {
            const loadingOverlay = document.getElementById('mapLoading');
            if (loadingOverlay) {
                loadingOverlay.innerHTML = `
                    <div class="alert alert-warning">
                        <i class="fas fa-exclamation-triangle"></i>
                        <strong>Map failed to load.</strong><br>
                        Please check your Google Maps API key configuration.
                    </div>
                `;
            }
        }
    }, 5000);
});
