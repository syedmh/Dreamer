$(document).ready(function () {
    // Initialize funnel fill on page load
    updateFunnelFill();

    // Handle donation form submission
    $('#donationForm').on('submit', function (e) {
        e.preventDefault();

        const amount = parseFloat($('#donationAmount').val());

        if (!amount || amount <= 0) {
            showMessage('Please enter a valid amount greater than zero.', 'danger');
            return;
        }

        // Disable button during submission
        const $submitBtn = $(this).find('button[type="submit"]');
        const originalText = $submitBtn.text();
        $submitBtn.prop('disabled', true).text('Processing...');

        // Submit donation via AJAX
        $.ajax({
            url: '/Donation/AddDonation',
            type: 'POST',
            contentType: 'application/json',
            headers: {
                'RequestVerificationToken': $('input[name="__RequestVerificationToken"]').val()
            },
            data: JSON.stringify(amount),
            success: function (response) {
                if (response.success) {
                    // Update total amount display
                    $('#totalAmount').text('$' + response.totalRaised.toLocaleString('en-US', {
                        minimumFractionDigits: 2,
                        maximumFractionDigits: 2
                    }));

                    // Update percentage display
                    $('#percentageText').text(response.percentage.toFixed(2) + '%');

                    // Update funnel fill with animation
                    updateFunnelFill(response.percentage, response.isOverflow, response.overflowAmount);

                    // Handle overflow display
                    if (response.isOverflow) {
                        showOverflow(response.overflowAmount, response.percentage - 100);
                    }

                    // Show success message
                    showMessage('Thank you for your donation of $' + amount.toFixed(2) + '!', 'success');

                    // Clear form
                    $('#donationAmount').val('');

                    // Add to recent donations list
                    addDonationToList(amount);

                    // Show fireworks if donation > $500
                    if (response.showFireworks) {
                        setTimeout(function () {
                            showFireworks();
                        }, 500);
                    }
                } else {
                    showMessage(response.message || 'An error occurred.', 'danger');
                }

                // Re-enable button
                $submitBtn.prop('disabled', false).text(originalText);
            },
            error: function () {
                showMessage('An error occurred while processing your donation.', 'danger');
                $submitBtn.prop('disabled', false).text(originalText);
            }
        });
    });

    // Update funnel fill visualization
    function updateFunnelFill(percentage, isOverflow, overflowAmount) {
        const funnelFill = document.getElementById('funnelFill');
        if (!funnelFill) return;

        // If percentage not provided, get from data attribute
        if (percentage === undefined) {
            percentage = parseFloat(funnelFill.getAttribute('data-percentage')) || 0;
        }

        // Cap fill percentage at 100% - overflow shown separately
        const fillPercentage = Math.min(percentage, 100);

        // Get max height from data attribute (fixed value from server)
        const maxHeight = parseFloat(funnelFill.getAttribute('data-max-height')) || 400;
        const fillHeight = (fillPercentage / 100) * maxHeight;
        const fillY = maxHeight - fillHeight;

        // Update the rectangle
        funnelFill.setAttribute('height', fillHeight);
        funnelFill.setAttribute('y', fillY);
        funnelFill.setAttribute('data-percentage', percentage);

        // Update overflow visual
        const overflowVisual = document.getElementById('overflowVisual');
        if (overflowVisual) {
            overflowVisual.setAttribute('opacity', isOverflow ? '1' : '0');
        }
    }

    // Show overflow indicator
    function showOverflow(overflowAmount, overflowPercentage) {
        const $indicator = $('#overflowIndicator');
        const $amount = $('#overflowAmount');

        // Update overflow amount
        $amount.text('+$' + overflowAmount.toLocaleString('en-US', {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2
        }));

        // Update overflow percentage
        $('.overflow-percent').text('(' + overflowPercentage.toFixed(1) + '% over goal)');

        // Show indicator with animation
        $indicator.fadeIn(500).addClass('show');
    }

    // Show message to user
    function showMessage(text, type) {
        const $message = $('#message');
        $message.removeClass('alert-success alert-danger alert-info')
            .addClass('alert alert-' + type + ' show')
            .text(text);

        // Hide after 5 seconds
        setTimeout(function () {
            $message.removeClass('show').fadeOut(function () {
                $(this).show().css('display', '');
            });
        }, 5000);
    }

    // Add donation to recent list
    function addDonationToList(amount) {
        const now = new Date();
        const timeStr = now.toLocaleString('en-US', {
            month: 'short',
            day: '2-digit',
            hour: '2-digit',
            minute: '2-digit',
            hour12: false
        });

        const listItem = $('<li>')
            .addClass('list-group-item d-flex justify-content-between align-items-center donation-added')
            .html(
                '<span class="donation-time">' + timeStr + '</span>' +
                '<span class="badge bg-primary rounded-pill">$' + amount.toFixed(2) + '</span>'
            );

        let $donationList = $('#donationList');

        // Create list if it doesn't exist
        if ($donationList.length === 0) {
            $('#recentDonations').html('<ul class="list-group" id="donationList"></ul>');
            $donationList = $('#donationList');
        }

        // Prepend new donation
        $donationList.prepend(listItem);

        // Keep only last 10 donations
        if ($donationList.children().length > 10) {
            $donationList.children().last().remove();
        }
    }

    // Allow only numbers and decimal in amount input
    $('#donationAmount').on('keypress', function (e) {
        const charCode = e.which ? e.which : e.keyCode;
        if (charCode > 31 && (charCode < 48 || charCode > 57) && charCode !== 46) {
            return false;
        }
        // Only allow one decimal point
        if (charCode === 46 && $(this).val().indexOf('.') !== -1) {
            return false;
        }
        return true;
    });
});
