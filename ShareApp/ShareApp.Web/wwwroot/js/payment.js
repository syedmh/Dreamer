// ShareApp - Stripe Payment Integration

let stripe;
let elements;
let paymentElement;

// Initialize Stripe (called when Stripe.js loads)
function initializeStripe(publishableKey) {
    stripe = Stripe(publishableKey);
}

// Initialize payment form
async function initializePaymentForm(transactionId, amount) {
    try {
        // Create payment intent
        const response = await fetch('/api/Payment/create-intent', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            body: JSON.stringify({
                transactionId: transactionId,
                amount: amount
            })
        });

        if (!response.ok) {
            throw new Error('Failed to create payment intent');
        }

        const { clientSecret, error } = await response.json();

        if (error) {
            showPaymentError(error);
            return;
        }

        // Create Stripe Elements
        const appearance = {
            theme: 'stripe',
            variables: {
                colorPrimary: '#0d6efd',
            }
        };

        elements = stripe.elements({ clientSecret, appearance });

        // Create and mount Payment Element
        paymentElement = elements.create('payment');
        paymentElement.mount('#payment-element');

        // Show payment form
        document.getElementById('payment-loading').style.display = 'none';
        document.getElementById('payment-form-container').style.display = 'block';

    } catch (error) {
        console.error('Error initializing payment:', error);
        showPaymentError(error.message);
    }
}

// Handle payment form submission
async function handlePaymentSubmit(event) {
    event.preventDefault();

    const submitButton = document.getElementById('payment-submit');
    const spinner = document.getElementById('payment-spinner');
    const buttonText = document.getElementById('payment-button-text');

    // Disable submit button
    submitButton.disabled = true;
    spinner.style.display = 'inline-block';
    buttonText.textContent = 'Processing...';

    try {
        const { error: submitError } = await elements.submit();
        if (submitError) {
            showPaymentError(submitError.message);
            resetPaymentButton(submitButton, spinner, buttonText);
            return;
        }

        // Confirm payment
        const { error } = await stripe.confirmPayment({
            elements,
            confirmParams: {
                return_url: window.location.origin + '/Transactions/PaymentSuccess?transactionId=' + document.getElementById('transactionId').value,
            },
        });

        if (error) {
            // Payment failed
            showPaymentError(error.message);
            resetPaymentButton(submitButton, spinner, buttonText);
        }
        // If no error, user will be redirected to return_url

    } catch (error) {
        console.error('Payment error:', error);
        showPaymentError('An unexpected error occurred. Please try again.');
        resetPaymentButton(submitButton, spinner, buttonText);
    }
}

// Show payment error message
function showPaymentError(message) {
    const errorElement = document.getElementById('payment-error');
    errorElement.textContent = message;
    errorElement.style.display = 'block';

    // Hide after 5 seconds
    setTimeout(() => {
        errorElement.style.display = 'none';
    }, 5000);
}

// Reset payment button state
function resetPaymentButton(button, spinner, text) {
    button.disabled = false;
    spinner.style.display = 'none';
    text.textContent = 'Pay Now';
}

// Show payment modal
function showPaymentModal(transactionId, amount) {
    const modal = new bootstrap.Modal(document.getElementById('paymentModal'));
    modal.show();

    // Initialize payment form when modal is shown
    document.getElementById('transactionId').value = transactionId;
    document.getElementById('payment-amount-display').textContent = '$' + amount.toFixed(2);

    initializePaymentForm(transactionId, amount);
}

// Cancel payment
function cancelPayment() {
    const modal = bootstrap.Modal.getInstance(document.getElementById('paymentModal'));
    modal.hide();

    // Reset form
    if (paymentElement) {
        paymentElement.unmount();
    }
    document.getElementById('payment-loading').style.display = 'block';
    document.getElementById('payment-form-container').style.display = 'none';
}
