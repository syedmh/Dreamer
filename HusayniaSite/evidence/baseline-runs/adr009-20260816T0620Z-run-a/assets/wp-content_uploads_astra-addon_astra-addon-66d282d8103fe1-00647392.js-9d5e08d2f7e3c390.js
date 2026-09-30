/**
 * File fronend-pro.js
 *
 * Handles toggling the navigation menu for Addon widget
 *
 * @package astra-addon
 */

 astraToggleSetupPro = function( mobileHeaderType, body, menu_click_listeners ) {

	var flag = false;
	var menuToggleAllLength;

	if ( 'off-canvas' === mobileHeaderType || 'full-width' === mobileHeaderType ) {
        // comma separated selector added, if menu is outside of Off-Canvas then submenu is not clickable, it work only for Off-Canvas area with dropdown style.
        var __main_header_all = document.querySelectorAll( '#ast-mobile-popup, #ast-mobile-header' );
        if ( body.classList.contains('ast-header-break-point') ) {

            var menu_toggle_all   = document.querySelectorAll( '#ast-mobile-header .main-header-menu-toggle' );
        } else {
            menu_toggle_all   = document.querySelectorAll( '#ast-desktop-header .main-header-menu-toggle' );
		}
		menuToggleAllLength = menu_toggle_all.length;
    } else {

        if ( body.classList.contains('ast-header-break-point') ) {

            var __main_header_all = document.querySelectorAll( '#ast-mobile-header' ),
                menu_toggle_all   = document.querySelectorAll( '#ast-mobile-header .main-header-menu-toggle' );
        } else {

            var __main_header_all = document.querySelectorAll( '#ast-desktop-header' ),
                menu_toggle_all   = document.querySelectorAll( '#ast-desktop-header .main-header-menu-toggle' );
		}
		menuToggleAllLength = menu_toggle_all.length;
		flag = menuToggleAllLength > 0 ? false : true;
		menuToggleAllLength = flag ? 1 : menuToggleAllLength;
    }

	if ( menuToggleAllLength > 0 || flag ) {

        for (var i = 0; i < menuToggleAllLength; i++) {

			if ( !flag ) {
				menu_toggle_all[i].setAttribute('data-index', i);

				if (!menu_click_listeners[i]) {
					menu_click_listeners[i] = menu_toggle_all[i];
					menu_toggle_all[i].addEventListener('click', astraNavMenuToggle, false);
				}
			}

            if ('undefined' !== typeof __main_header_all[i]) {

                // To handle the comma seprated selector added above we need this loop.
                for( var mainHeaderCount =0; mainHeaderCount  < __main_header_all.length; mainHeaderCount++ ){

                    if (document.querySelector('header.site-header').classList.contains('ast-builder-menu-toggle-link')) {
                        var astra_menu_toggle = __main_header_all[mainHeaderCount].querySelectorAll('ul.main-header-menu .menu-item-has-children > .menu-link, ul.main-header-menu .ast-menu-toggle');
                    } else {
                        var astra_menu_toggle = __main_header_all[mainHeaderCount].querySelectorAll('ul.main-header-menu .ast-menu-toggle');
                    }
                    // Add Eventlisteners for Submenu.
                    if (astra_menu_toggle.length > 0) {

                        for (var j = 0; j < astra_menu_toggle.length; j++) {

                            astra_menu_toggle[j].addEventListener('click', AstraToggleSubMenu, false);
                        }
                    }
                }
            }
        }
    }
}

astraNavMenuTogglePro = function ( event, body, mobileHeaderType, thisObj ) {

    event.preventDefault();

    var desktop_header = event.target.closest('#ast-desktop-header');

    var desktop_header_content = document.querySelector('#masthead > #ast-desktop-header .ast-desktop-header-content');

    if ( null !== desktop_header && undefined !== desktop_header && '' !== desktop_header ) {

        var desktop_toggle = desktop_header.querySelector( '.main-header-menu-toggle' );
    } else {
        var desktop_toggle = document.querySelector('#masthead > #ast-desktop-header .main-header-menu-toggle');
    }

    var desktop_menu = document.querySelector('#masthead > #ast-desktop-header .ast-desktop-header-content .main-header-bar-navigation');

    if ( 'desktop' === event.currentTarget.trigger_type ) {

        if ( null !== desktop_menu && '' !== desktop_menu && undefined !== desktop_menu ) {
            astraToggleClass(desktop_menu, 'toggle-on');
            if (desktop_menu.classList.contains('toggle-on')) {
                desktop_menu.style.display = 'block';
            } else {
                desktop_menu.style.display = '';
            }
        }
        astraToggleClass(desktop_toggle, 'toggled');
        if ( desktop_toggle.classList.contains( 'toggled' ) ) {
            body.classList.add("ast-main-header-nav-open");
            if ( 'dropdown' === mobileHeaderType ) {
                desktop_header_content.style.display = 'block';
            }
        } else {
            body.classList.remove("ast-main-header-nav-open");
            desktop_header_content.style.display = 'none';
        }
        return;
    }

    var __main_header_all = document.querySelectorAll('#masthead > #ast-mobile-header .main-header-bar-navigation');
    menu_toggle_all 	 = document.querySelectorAll( '#masthead > #ast-mobile-header .main-header-menu-toggle' )
    var event_index = '0';
    var sticky_header = false;
    if ( null !== thisObj.closest( '#ast-fixed-header' ) ) {

        __main_header_all = document.querySelectorAll('#ast-fixed-header > #ast-mobile-header .main-header-bar-navigation');
        menu_toggle_all 	 = document.querySelectorAll( '#ast-fixed-header .main-header-menu-toggle' )

        event_index = '0';
        sticky_header = true;

    }

    if ('undefined' === typeof __main_header_all[event_index]) {
        return false;
    }
    var menuHasChildren = __main_header_all[event_index].querySelectorAll('.menu-item-has-children');
    for (var i = 0; i < menuHasChildren.length; i++) {
        menuHasChildren[i].classList.remove('ast-submenu-expanded');
        var menuHasChildrenSubMenu = menuHasChildren[i].querySelectorAll('.sub-menu');
        for (var j = 0; j < menuHasChildrenSubMenu.length; j++) {
            menuHasChildrenSubMenu[j].style.display = 'none';
        }
    }

    var menu_class = thisObj.getAttribute('class') || '';

    if ( menu_class.indexOf('main-header-menu-toggle') !== -1 ) {
        astraToggleClass(__main_header_all[event_index], 'toggle-on');
        astraToggleClass(menu_toggle_all[event_index], 'toggled');
        if ( sticky_header && 1 < menu_toggle_all.length ) {
            astraToggleClass(menu_toggle_all['1'], 'toggled');
        }
        if (__main_header_all[event_index].classList.contains('toggle-on')) {
            __main_header_all[event_index].style.display = 'block';
            body.classList.add("ast-main-header-nav-open");
        } else {
            __main_header_all[event_index].style.display = '';
            body.classList.remove("ast-main-header-nav-open");
        }
    }
}
/**
 * Stick elements
 *
 * => How to use?
 *
 * jQuery( {SELECTOR} ).astHookExtSticky( {
 *		dependent: [{selectors}], 	// Not required. Default: []. Stick element dependent selectors.
 *		stick_upto_scroll: {value}, 	// Not required. Default: 0. Stick element after scroll upto the {value} in px.
 *		gutter: {value}, 			// Not required. Default: 0. Stick element from top of the window in px\.
 * });
 *
 * @package Astra Addon
 * @since  1.0.0
 */

;(function ( $, window, undefined ) {

	var pluginName    = 'astHookExtSticky',
		document      = window.document,
		windowWidth   = jQuery( window ).outerWidth(),
		viewPortWidth = jQuery( window ).width(),
		defaults      = {
			dependent            : [],
			max_width            : '',
			site_layout          : '',
			break_point          : 920,
			admin_bar_height_lg  : 32,
			admin_bar_height_sm  : 46,
			admin_bar_height_xs  : 0,
			stick_upto_scroll    : 0,
			gutter               : 0,
			wrap                 : '<div></div>',

			// Padding support of <body> tag.
			body_padding_support : true,

			// Padding support of <html> tag.
			html_padding_support : true,

			active_shrink : false,
			// Added shrink option.
			shrink               : {
									padding_top    : '',
									padding_bottom : '',
						    	},

			// Enable sticky on mobile
			sticky_on_device 	 : 'desktop',

			header_style 		 : 'none',

			hide_on_scroll 		 : 'no',
		},
		/* Manage hide on scroll down */
		lastScrollTop 		= 0,
		delta 				= 5,
		navbarHeight 		= 0,
		should_stick		= true,
		hideScrollInterval;

	/**
	 * Init
	 *
	 * @since  1.0.0
	 */
	function astHookExtSticky( element, options ) {
		this.element   = element;
		this.options   = $.extend( {}, defaults, options );
		this._defaults = defaults;
		this._name     = pluginName;

		/* Manage hide on scroll down */
		if ( '1' == this.options.hide_on_scroll ) {
			this.navbarHeight = $(element).outerHeight();
		}

		this.lastScrollTop 		= 0;
		this.delta 				= 5;
		this.should_stick		= true;
		this.hideScrollInterval = '';

		this.init();
	}

	/**
	 * Stick element
	 *
	 * @since  1.0.0
	 */
	astHookExtSticky.prototype.stick_me = function( self, type ) {

		var selector      	  = jQuery( self.element ),
			windowWidth       = jQuery( window ).outerWidth(),
			stick_upto_scroll = parseInt( self.options.stick_upto_scroll ),
			max_width         = parseInt( selector.parent().attr( 'data-stick-maxwidth' ) ), // parseInt( self.options.max_width ),
			gutter            = parseInt( selector.parent().attr( 'data-stick-gutter' ) ); // parseInt( self.options.gutter ).
		/**
		 * Check window width
		 */
		 var hook_sticky_header = astraAddon.hook_sticky_header || '';
		 // Any stick header is enabled?
		if ( 'enabled' == hook_sticky_header ) {
			if ( 'desktop' == self.options.sticky_on_device && ( astraAddon.hook_custom_header_break_point > windowWidth ) ) {
				self.stickRelease( self );
			} else if ( 'mobile' == self.options.sticky_on_device && ( astraAddon.hook_custom_header_break_point <= windowWidth ) ) {
				self.stickRelease( self );
			} else {
				if ( jQuery( window ).scrollTop() > stick_upto_scroll ) {
					
					if ( 'none' == self.options.header_style ) {
						if ( 'enabled' == self.options.active_shrink ) {
							self.hasShrink( self, 'stick' );
						}
						if( selector.hasClass( 'ast-custom-header' ) ){
							selector.parent().css( 'min-height', selector.outerHeight() );
							selector.addClass( 'ast-header-sticky-active' ).stop().css({
								'max-width'      : max_width,
								'top'            : gutter,
								'padding-top'    : self.options.shrink.padding_top,
								'padding-bottom' : self.options.shrink.padding_bottom,
							});
							selector.addClass( 'ast-sticky-shrunk' ).stop();
						}
					}
				} else {
					self.stickRelease( self );
				}
			}
		}

		var hook_sticky_footer = astraAddon.hook_sticky_footer || '';
		// Any stick header is enabled?
		if ( 'enabled' == hook_sticky_footer ) {

			if ( 'desktop' == self.options.sticky_on_device && ( astraAddon.hook_custom_footer_break_point > windowWidth ) ) {
				self.stickRelease( self );
			} else if ( 'mobile' == self.options.sticky_on_device && ( astraAddon.hook_custom_footer_break_point <= windowWidth ) ) {
				self.stickRelease( self );
			}
			else{
				jQuery( 'body' ).addClass( 'ast-footer-sticky-active' );
				selector.parent().css( 'min-height', selector.outerHeight() );
				selector.stop().css({
					'max-width'      : max_width,
				});
			}
		}
	}

	astHookExtSticky.prototype.update_attrs = function () {

		var self  	          = this,
			selector          = jQuery( self.element ),
			gutter            = parseInt( self.options.gutter ),
			max_width         = self.options.max_width;

		if ( 'none' == self.options.header_style ) {
			var stick_upto_scroll = selector.offset().top || 0;
		}

		/**
		 * Update Max-Width
		 */
		if ( 'ast-box-layout' != self.options.site_layout ) {
			max_width = jQuery( 'body' ).width();
		}

		/**
		 * Check dependent element
		 * - Is exist?
		 * - Has attr 'data-stick-support' with status 'on'
		 */
		if ( self.options.dependent ) {
			jQuery.each( self.options.dependent, function(index, val) {
				if (
					( jQuery( val ).length ) &&
					( jQuery( val ).parent().attr( 'data-stick-support' ) == 'on' )
				) {
					dependent_height   = jQuery( val ).outerHeight();
					gutter            += parseInt( dependent_height );
					stick_upto_scroll -= parseInt( dependent_height );
				}
			});
		}

		/**
		 * Add support for Admin bar height
		 */
		if ( self.options.admin_bar_height_lg && jQuery( '#wpadminbar' ).length && viewPortWidth > 782 ) {
			gutter            += parseInt( self.options.admin_bar_height_lg );
			stick_upto_scroll -= parseInt( self.options.admin_bar_height_lg );
		}

		if ( self.options.admin_bar_height_sm && jQuery( '#wpadminbar' ).length && ( viewPortWidth >= 600 && viewPortWidth <= 782 ) ) {
			gutter            += parseInt( self.options.admin_bar_height_sm );
			stick_upto_scroll -= parseInt( self.options.admin_bar_height_sm );
		}

		if( self.options.admin_bar_height_xs && jQuery( '#wpadminbar' ).length ){
			gutter            += parseInt( self.options.admin_bar_height_xs );
			stick_upto_scroll -= parseInt( self.options.admin_bar_height_xs );
		}

		/**
		 * Add support for <body> tag
		 */
		if ( self.options.body_padding_support ) {
			gutter            += parseInt( jQuery( 'body' ).css( 'padding-top' ), 10 );
			stick_upto_scroll -= parseInt( jQuery( 'body' ).css( 'padding-top' ), 10 );
		}

		/**
		 * Add support for <html> tag
		 */
		if ( self.options.html_padding_support ) {
			gutter            += parseInt( jQuery( 'html' ).css( 'padding-top' ), 10 );
			stick_upto_scroll -= parseInt( jQuery( 'html' ).css( 'padding-top' ), 10 );
		}

		/**
		 * Updated vars
		 */
		self.options.stick_upto_scroll = stick_upto_scroll;

		/**
		 * Update Attributes
		 */
		if ( 'none' == self.options.header_style ) {
			selector.parent()
				.css( 'min-height', selector.outerHeight() )
				.attr( 'data-stick-gutter', parseInt( gutter ) )
				.attr( 'data-stick-maxwidth', parseInt( max_width ) );
		}
	}

	astHookExtSticky.prototype.hasShrink = function( self, method ) {
		
		var st = $( window ).scrollTop();

	    // If they scrolled down and are past the navbar, add class .nav-up.
	    // This is necessary so you never see what is "behind" the navbar.
		var fixed_header = jQuery(self.element);
	    if ( st > fixed_header.outerHeight() ){
	        // Active Shrink
	        jQuery('body').addClass('ast-shrink-custom-header');
	    } else {
	        // Remove Shrink effect
	        jQuery('body').removeClass('ast-shrink-custom-header');
	    }
	}

	astHookExtSticky.prototype.stickRelease = function( self ) {
		var selector = jQuery( self.element );
		
		var hook_sticky_header = astraAddon.hook_sticky_header || '';
		 // Any stick header is enabled?
		if ( 'enabled' == hook_sticky_header ) {
			if ( 'none' == self.options.header_style ) {
				selector.removeClass( 'ast-header-sticky-active' ).stop().css({
					'max-width' : '',
					'top'		: '',
					'padding'	: '',
				});
				selector.parent().css( 'min-height', '' );
				selector.removeClass( 'ast-sticky-shrunk' ).stop();
			}
		}

		var hook_sticky_footer = astraAddon.hook_sticky_footer || '';
		 // Any stick footer is enabled?
		if ( 'enabled' == hook_sticky_footer ) {
			jQuery( 'body' ).removeClass( 'ast-footer-sticky-active' );
		}
	}
	/**
	 * Init Prototype
	 *
	 * @since  1.0.0
	 */
	astHookExtSticky.prototype.init = function () {

		/**
		 * If custom stick options are set
		 */
		if ( jQuery( this.element ) ) {

			var self                       	   = this,
				selector                       = jQuery( self.element ),
				gutter                         = parseInt( self.options.gutter ),
				stick_upto_scroll              = selector.position().top || 0,
				dependent_height               = 0;

			/**
			 *	Add parent <div> wrapper with height element for smooth scroll
			 *
			 *	Added 'data-stick-support' to all sticky elements
			 *	To know the {dependent} element has support of 'stick'
			 */
			 if ( 'none' == self.options.header_style ) {
				selector.wrap( self.options.wrap )
					.parent().css( 'min-height', selector.outerHeight() )
					.attr( 'data-stick-support', 'on' )
					.attr( 'data-stick-maxwidth', parseInt( self.options.max_width ) );
			}

			self.update_attrs();

			// Stick me!.
			jQuery( window ).on('resize', function() {

				self.stickRelease( self );
				self.update_attrs();
				self.stick_me( self );
			} );

			jQuery( window ).on('scroll', function() {
				// update the stick_upto_scroll if normal main header navigation is opend.
				self.stick_me( self, 'scroll' );
			} );

			jQuery( document ).ready(function($) {
				self.stick_me( self );
			} );

		}

	};

	$.fn[pluginName] = function ( options ) {
		return this.each(function () {
			if ( ! $.data( this, 'plugin_' + pluginName )) {
				$.data( this, 'plugin_' + pluginName, new astHookExtSticky( this, options ) );
			}
		});
	}



	var $body = jQuery( 'body' ),
		layout_width             = $body.width(),
		site_layout              = astraAddon.site_layout || '',
		hook_sticky_header = astraAddon.hook_sticky_header || '',
		hook_shrink_header = astraAddon.hook_shrink_header || '';
		sticky_header_on_devices = astraAddon.hook_sticky_header_on_devices || 'desktop',
		site_layout_box_width    = astraAddon.site_layout_box_width || 1200,
		hook_sticky_footer = astraAddon.hook_sticky_footer || '',
		sticky_footer_on_devices = astraAddon.hook_sticky_footer_on_devices || 'desktop';



		switch ( site_layout ) {
			case 'ast-box-layout':
				layout_width = parseInt( site_layout_box_width );
			break;
		}

		jQuery( document ).ready(function($) {
			// Any stick header is enabled?
			if ( 'enabled' == hook_sticky_header ) {

				jQuery( '.ast-custom-header' ).astHookExtSticky({
					sticky_on_device: sticky_header_on_devices,
					header_style: 'none',
					site_layout: site_layout,
					max_width: layout_width,
					active_shrink: hook_shrink_header,
				});

			}

			// Any stick footer is enabled?
			if ( 'enabled' == hook_sticky_footer ) {

				jQuery( '.ast-custom-footer' ).astHookExtSticky({
					sticky_on_device: sticky_footer_on_devices,
					max_width: layout_width,
					site_layout: site_layout,
					header_style: 'none',
				});

			}
	    });

}(jQuery, window));
/**
 * Stick elements
 *
 * => How to use?
 *
 * jQuery( {SELECTOR} ).astExtSticky( {
 *		dependent: [{selectors}], 	// Not required. Default: []. Stick element dependent selectors.
 *		stick_upto_scroll: {value}, 	// Not required. Default: 0. Stick element after scroll upto the {value} in px.
 *		gutter: {value}, 			// Not required. Default: 0. Stick element from top of the window in px\.
 * });
 *
 * @package Astra Addon
 * @since  1.0.0
 */

;(function ( $, window, undefined ) {

	var pluginName    = 'astExtSticky',
		document      = window.document,
		windowWidth   = jQuery( window ).outerWidth(),
		viewPortWidth = jQuery( window ).width(),
		header_builder_active = astraAddon.header_builder_active,
		defaults      = {
			dependent            : [],
			max_width            : '',
			site_layout          : '',
			break_point          : 920,
			admin_bar_height_lg  : 32,
			admin_bar_height_sm  : 46,
			admin_bar_height_xs  : 0,
			stick_upto_scroll    : 0,
			gutter               : 0,
			wrap                 : '<div></div>',

			// Padding support of <body> tag.
			body_padding_support : true,

			// Padding support of <html> tag.
			html_padding_support : true,

			// Added shrink option.
			shrink               : {
									padding_top    : '',
									padding_bottom : '',
						    	},

			// Enable sticky on mobile
			sticky_on_device 	 : 'desktop',

			header_style 		 : 'none',

			hide_on_scroll 		 : 'no',
		},
		/* Manage hide on scroll down */
		lastScrollTop 		= 0,
		delta 				= 5,
		navbarHeight 		= 0,
		should_stick		= true,
		hideScrollInterval;

	/**
	 * Init
	 *
	 * @since  1.0.0
	 */
	function astExtSticky( element, options ) {
		this.element   = element;
		this.options   = $.extend( {}, defaults, options );
		this._defaults = defaults;
		this._name     = pluginName;

		/* Manage hide on scroll down */
		if ( '1' == this.options.hide_on_scroll ) {
			this.navbarHeight = $(element).outerHeight();
		}

		this.lastScrollTop 		= 0;
		this.delta 				= 5;
		this.should_stick		= true;
		this.hideScrollInterval = '';

		this.init();
	}

	/**
	 * Stick element
	 *
	 * @since  1.0.0
	 */
	astExtSticky.prototype.stick_me = function( self, type ) {

		var selector      	  = jQuery( self.element ),
			windowWidth       = jQuery( window ).outerWidth();
			stick_upto_scroll = parseInt( self.options.stick_upto_scroll ),
			max_width         = parseInt( selector.parent().attr( 'data-stick-maxwidth' ) ), // parseInt( self.options.max_width ),
			gutter            = parseInt( selector.parent().attr( 'data-stick-gutter' ) ), // parseInt( self.options.gutter ).
			aboveHeaderSelectorValue = gutter;

		if ( header_builder_active && astraAddon.header_main_shrink ) {
			// Decrese the top of primary / below as we decrease the min-height of all sticked headers by 20.
			if ( ( selector.hasClass( 'ast-stick-primary-below-wrapper' ) || ( selector.hasClass( 'ast-primary-header' ) ) ) && 1 == astraAddon.header_above_stick && gutter > 0  ) {

				gutter = gutter - 10;
			}

			var aboveHeaderSelector = document.querySelector('.ast-above-header-bar');
			if ( 1 == astraAddon.header_above_stick && null !== aboveHeaderSelector ) {
				aboveHeaderSelectorValue = aboveHeaderSelector.getBoundingClientRect().height + parseInt( aboveHeaderSelector.parentNode.getAttribute( 'data-stick-gutter' ) );
			}
		}

		/**
		 * Check window width
		 */
		if ( 'desktop' == self.options.sticky_on_device && jQuery( 'body' ).hasClass( 'ast-header-break-point' ) ) {
			self.stickRelease( self );
		} else if ( 'mobile' == self.options.sticky_on_device && ! jQuery( 'body' ).hasClass( 'ast-header-break-point' ) ) {
			self.stickRelease( self );
		} else {

			// stick_upto_scroll with negative value enables a sticky by default so rounding up to zero.
			if ( stick_upto_scroll < 0 ) {
				stick_upto_scroll = 0;
			}

			// Check if the Elementor Motion Effect class present
			var stcikyHeaderElementor = document.getElementsByClassName('elementor-motion-effects-parent');
			var stickyHeaderFlag = stcikyHeaderElementor.length > 0 ? true : false;

			if ( jQuery( window ).scrollTop() > stick_upto_scroll ) {

				var fixed_header = selector;

				if ( header_builder_active ) {

					var mobile_parent = selector.closest( '.ast-mobile-header-wrap' );
					var desktop_parent = selector.closest( '#ast-desktop-header' );

					mobile_parent = ( 0 === mobile_parent.length ) ? selector.find( '.ast-mobile-header-wrap' ) : mobile_parent;

					desktop_parent = ( 0 === desktop_parent.length ) ? selector.find( '#ast-desktop-header' ) : desktop_parent;

					mobile_parent.find( '.ast-mobile-header-content' ).css( 'top', selector.outerHeight() + gutter );
					if ( 'ast-box-layout' == self.options.site_layout ) {
					    var max_width_mobile = jQuery( 'body' ).width();
						mobile_parent.find( '.ast-mobile-header-content' ).css( 'width', max_width_mobile );
					} else {
						mobile_parent.find( '.ast-mobile-header-content' ).css( 'width', max_width );
					}

					desktop_parent.find( '.ast-desktop-header-content' ).css( 'top', selector.outerHeight() + gutter );
					desktop_parent.find( '.ast-desktop-header-content' ).css( 'width', max_width );
				}

				if ( '1' === self.options.hide_on_scroll ) {

					self.hasScrolled( self, 'stick' );
				}else if ( 'none' == self.options.header_style ) {

					if ( ! stickyHeaderFlag ) {
						selector.parent().css( 'min-height', selector.outerHeight() );
					}

					if ( ! document.querySelector('body').classList.contains( 'fl-builder-edit' ) ) {
						selector.addClass('ast-sticky-active').stop().css({
							'top': gutter,
						});
					}
					selector.addClass( 'ast-sticky-active' ).stop().css({
						'max-width'      : max_width,
						'padding-top'    : self.options.shrink.padding_top,
						'padding-bottom' : self.options.shrink.padding_bottom,
					});
					if ( ( selector.hasClass( 'ast-stick-primary-below-wrapper' ) || selector.hasClass( 'ast-primary-header' ) ) && 1 == astraAddon.header_above_stick && 70 > selector.closest('#ast-desktop-header').find('.ast-above-header-bar').outerHeight() ) {

						selector.addClass( 'ast-sticky-active' ).stop().css({
							'top'            : stickyHeaderFlag ? aboveHeaderSelectorValue : 'unset',
						});

						selector.parent().css( 'min-height', 'unset' );
					}

					selector.addClass( 'ast-sticky-shrunk' ).stop();
					$( document ).trigger( "addStickyClass" );
					fixed_header.addClass('ast-header-sticked');

				}else if ( 'slide' == self.options.header_style ) {
					fixed_header.css({
						'top' : gutter,
					});
					fixed_header.addClass('ast-header-slide');
					fixed_header.css( 'visibility', 'visible' );
					fixed_header.addClass( 'ast-sticky-active' ).stop().css({
						'transform':'translateY(0)',
					});
					$('html').addClass('ast-header-stick-slide-active');
					$( document ).trigger( "addStickyClass" );
					fixed_header.addClass('ast-header-sticked');

				}else if( 'fade' == self.options.header_style ) {
					fixed_header.css({
						'top' : gutter,
					});
					fixed_header.addClass('ast-header-fade');
					fixed_header.css( 'visibility', 'visible' );
					fixed_header.addClass( 'ast-sticky-active' ).stop().css({
						'opacity' : '1',
					});
					$('html').addClass('ast-header-stick-fade-active');
					$( document ).trigger( "addStickyClass" );
					fixed_header.addClass('ast-header-sticked');
				}
			} else {
				self.stickRelease( self );
				if ( header_builder_active ) {
					var mobile_parent = selector.closest( '.ast-mobile-header-wrap' );
					mobile_parent = ( 0 === mobile_parent.length ) ? selector.find( '.ast-mobile-header-wrap' ) : mobile_parent;
					if ( !jQuery( 'body' ).hasClass( 'ast-primary-sticky-header-active' ) || !jQuery( 'body' ).hasClass( 'ast-above-sticky-header-active' ) || !jQuery( 'body' ).hasClass( 'ast-below-sticky-header-active' ) ) {
						mobile_parent.find( '.ast-mobile-header-content' ).removeAttr( 'style' );
					}
				}
			}
		}
	}

	astExtSticky.prototype.update_attrs = function () {

		var self  	          = this,
			selector          = jQuery( self.element ),
			gutter            = parseInt( self.options.gutter ),
			max_width         = self.options.max_width;

		if ( 'none' == self.options.header_style && ! jQuery( 'body' ).hasClass( 'ast-sticky-toggled-off' ) ) {
			var stick_upto_scroll = selector.offset().top || 0;
		}else{
			if ( $('#masthead').length ) {
				var masthead 			= $('#masthead');
				var masthead_bottom 	= masthead.offset().top + masthead.outerHeight() + 100;
				var stick_upto_scroll 	= masthead_bottom || 0;
			}
		}

		/**
		 * Update Max-Width
		 */
		if ( 'ast-box-layout' != self.options.site_layout ) {
			max_width = jQuery( 'body' ).width();
		}

		/**
		 * Check dependent element
		 * - Is exist?
		 * - Has attr 'data-stick-support' with status 'on'
		 */
		if ( self.options.dependent ) {
			jQuery.each( self.options.dependent, function(index, val) {
				if (
					( jQuery( val ).length ) &&
					( jQuery( val ).parent().attr( 'data-stick-support' ) == 'on' )
				) {

					dependent_height   = jQuery( val ).outerHeight();
					gutter            += parseInt( dependent_height );
					stick_upto_scroll -= parseInt( dependent_height );
				}
			});
		}

		/**
		 * Add support for Admin bar height
		 */
		if ( self.options.admin_bar_height_lg && jQuery( '#wpadminbar' ).length && viewPortWidth > 782 ) {
			gutter            += parseInt( self.options.admin_bar_height_lg );
			stick_upto_scroll -= parseInt( self.options.admin_bar_height_lg );
		}

		if ( self.options.admin_bar_height_sm && jQuery( '#wpadminbar' ).length && ( viewPortWidth >= 600 && viewPortWidth <= 782 ) ) {
			gutter            += parseInt( self.options.admin_bar_height_sm );
			stick_upto_scroll -= parseInt( self.options.admin_bar_height_sm );
		}

		if( self.options.admin_bar_height_xs && jQuery( '#wpadminbar' ).length ){
			gutter            += parseInt( self.options.admin_bar_height_xs );
			stick_upto_scroll -= parseInt( self.options.admin_bar_height_xs );
		}

		/**
		 * Add support for <body> tag
		 */
		if ( self.options.body_padding_support ) {
			gutter            += parseInt( jQuery( 'body' ).css( 'padding-top' ), 10 );
			stick_upto_scroll -= parseInt( jQuery( 'body' ).css( 'padding-top' ), 10 );
		}

		/**
		 * Add support for <html> tag
		 */
		if ( self.options.html_padding_support ) {
			gutter            += parseInt( jQuery( 'html' ).css( 'padding-top' ), 10 );
			stick_upto_scroll -= parseInt( jQuery( 'html' ).css( 'padding-top' ), 10 );
		}

		/**
		 * Reduce the stick_upto_scrll by one if filter `astra_addon_sticky_header_stick_origin_position` is set true.
		 * This will make the sticky header appear sticky on initial load.
		 */
		if ( stick_origin_position ) {
			stick_upto_scroll--;
		}

		/**
		 * Updated vars
		 */
		self.options.stick_upto_scroll = stick_upto_scroll;

		/**
		 * Update Attributes
		 */
		if ( 'none' == self.options.header_style ) {
			selector.parent()
				.css( 'min-height', selector.outerHeight() )
				.attr( 'data-stick-gutter', parseInt( gutter ) )
				.attr( 'data-stick-maxwidth', parseInt( max_width ) );
		}else{
			selector.parent()
				.attr( 'data-stick-gutter', parseInt( gutter ) )
				.attr( 'data-stick-maxwidth', parseInt( max_width ) );

			if ( 'ast-padded-layout' === self.options.site_layout ) {
				selector.css( 'max-width', parseInt( max_width ) );
			}
		}
	}

	astExtSticky.prototype.hasScrolled = function( self, method ) {

		var st = $( window ).scrollTop();

	    // Make sure they scroll more than delta
	    if(Math.abs(lastScrollTop - st) <= delta)
	        return;

	    // If they scrolled down and are past the navbar, add class .nav-up.
	    // This is necessary so you never see what is "behind" the navbar.
		var fixed_header = jQuery(self.element);
	    if (st > lastScrollTop && st > navbarHeight){

	        // Scroll Down
	        jQuery(self.element).removeClass('ast-nav-down').addClass('ast-nav-up');
	    } else {
	        // Scroll Up
	        if(st + $(window).height() < $(document).height()) {
	            jQuery(self.element).removeClass('ast-nav-up').addClass('ast-nav-down');
	        }
	    }

	    lastScrollTop = st;

	    if ( !$(self.element).hasClass('ast-nav-up') && 'stick' == method ) {
			fixed_header.css({
				'top' : gutter,
			});
			fixed_header.addClass('ast-header-sticked');
			fixed_header.addClass('ast-header-slide');
			fixed_header.css( 'visibility', 'visible' );
			fixed_header.addClass( 'ast-sticky-active' ).stop().css({
				'transform':'translateY(0)',
			});
			$( document ).trigger( "addStickyClass" );
			$('html').addClass('ast-header-stick-scroll-active');
		}else{
			fixed_header.css({
				'transform':'translateY(-100%)',
			}).stop();

			setTimeout(function() {
				fixed_header.removeClass( 'ast-sticky-active' );
			}, 300);
			fixed_header.css({
				'visibility' : 'hidden',
				'top' : '',
			});
			$( document ).trigger( "removeStickyClass" );
			$('html').removeClass('ast-header-stick-scroll-active');
			fixed_header.removeClass('ast-header-sticked');
		}
	}

	astExtSticky.prototype.stickRelease = function( self ) {
		var selector = jQuery( self.element );
		var fixed_header = selector;
		if ( '1' === self.options.hide_on_scroll ) {
			self.hasScrolled( self, 'release' );
		}else{
			if ( 'none' == self.options.header_style ) {
				selector.removeClass( 'ast-sticky-active' ).stop().css({
					'max-width' : '',
					'top'		: '',
					'padding'	: '',
				});
				selector.parent().css( 'min-height', '' );
				$( document ).trigger( "removeStickyClass" );
				fixed_header.removeClass('ast-header-sticked');
				selector.removeClass( 'ast-sticky-shrunk' ).stop();

			}else if ( 'slide' == self.options.header_style ) {
				fixed_header.removeClass( 'ast-sticky-active' ).stop().css({
					'transform':'translateY(-100%)',
				});
				fixed_header.css({
					'visibility' : 'hidden',
					'top' : '',
				});

				$('html').removeClass('ast-header-stick-slide-active');
				$( document ).trigger( "removeStickyClass" );
				fixed_header.removeClass('ast-header-sticked');

			}else if( 'fade' == self.options.header_style ) {
				fixed_header.removeClass( 'ast-sticky-active' ).stop().css({
					'opacity' : '0',
				});
				fixed_header.css({
					'visibility' : 'hidden',
				});
				fixed_header.removeClass('ast-header-sticked');
				$( document ).trigger( "removeStickyClass" );
				$('html').removeClass('ast-header-stick-fade-active');
			}
		}
	}
	/**
	 * Init Prototype
	 *
	 * @since  1.0.0
	 */
	astExtSticky.prototype.init = function () {

		/**
		 * If custom stick options are set
		 */
		if ( jQuery( this.element ) ) {

			var self                       	   = this,
				selector                       = jQuery( self.element );

			/**
			 *	Add parent <div> wrapper with height element for smooth scroll
			 *
			 *	Added 'data-stick-support' to all sticky elements
			 *	To know the {dependent} element has support of 'stick'
			 */
			 if ( 'none' == self.options.header_style ) {
				selector.wrap( self.options.wrap )
					.parent().css( 'min-height', selector.outerHeight() )
					.attr( 'data-stick-support', 'on' )
					.attr( 'data-stick-maxwidth', parseInt( self.options.max_width ) );
			}else{
				selector.wrap( self.options.wrap )
					.attr( 'data-stick-support', 'on' )
					.attr( 'data-stick-maxwidth', parseInt( self.options.max_width ) );
			}

			self.update_attrs();

			// Stick me!.
			jQuery( window ).on('resize', function() {

				self.stickRelease( self );
				self.update_attrs();
				self.stick_me( self );
			} );

			jQuery( window ).on('scroll', function() {
				// update the stick_upto_scroll if normal main header navigation is opend.
				self.stick_me( self, 'scroll' );

				if( jQuery( 'body' ).hasClass( 'ast-sticky-toggled-off' ) ){
					self.update_attrs();
					self.stick_me( self, 'scroll' );
				}
			} );

			jQuery( document ).ready(function($) {
				self.stick_me( self );
			} );
		}
	};

	$.fn[pluginName] = function ( options ) {
		return this.each(function () {
			if ( ! $.data( this, 'plugin_' + pluginName )) {
				$.data( this, 'plugin_' + pluginName, new astExtSticky( this, options ) );
			}
		});
	}

	var $body = jQuery( 'body' ),
		layout_width             = $body.width(),
		stick_header_meta        = astraAddon.stick_header_meta || 'default',
		stick_main               = astraAddon.header_main_stick || '',
		main_shrink              = astraAddon.header_main_shrink || '',
		stick_above              = astraAddon.header_above_stick || '',
		stick_below              = astraAddon.header_below_stick || '',
		header_main_stick_meta   = astraAddon.header_main_stick_meta || '',
		header_above_stick_meta  = astraAddon.header_above_stick_meta || '',
		header_below_stick_meta  = astraAddon.header_below_stick_meta || '',
		site_layout              = astraAddon.site_layout || '',
		site_layout_box_width    = astraAddon.site_layout_box_width || 1200,
		sticky_header_on_devices = astraAddon.sticky_header_on_devices || 'desktop',
		sticky_header_style		 = astraAddon.sticky_header_style || 'none',
		sticky_hide_on_scroll 	 = astraAddon.sticky_hide_on_scroll || '',
		header_logo_width    	 = astraAddon.header_logo_width || '',
		responsive_header_logo_width = astraAddon.responsive_header_logo_width || '',
		stick_origin_position    = astraAddon.stick_origin_position || '',
		tablet_break_point    = astraAddon.tablet_break_point || 768,
		mobile_break_point    = astraAddon.mobile_break_point || 544;

	/**
	 * Check meta options
	 */
	if ( 'disabled' == stick_header_meta ) {
		return;
	}

	if ( 'enabled' === stick_header_meta ) {
		stick_main = header_main_stick_meta;
		stick_above  = header_above_stick_meta;
		stick_below = header_below_stick_meta;
	}

	if ( $('header .site-logo-img img').length > 0 ) {
		var id_img 		= $('header .site-logo-img img');
		var id_height 	= id_img.attr('height');

		if ( typeof id_height === 'undefined' ) {
			id_height 	= id_img.height();
		}

		if ( id_height == 0 ) {
			id_height = '';
		}

		if ( -1 === id_height.toString().indexOf('%') ) {
			id_height += 'px';
		}

		if ( '' != responsive_header_logo_width.desktop || '' != responsive_header_logo_width.tablet ||  '' != responsive_header_logo_width.mobile  ) {
		var output = "<style type='text/css' id='ast-site-identity-img' class='ast-site-identity-img' > #masthead .ast-header-sticked .site-logo-img .astra-logo-svg { width: " + responsive_header_logo_width.desktop + "px; } @media (max-width: " + tablet_break_point + "px) { #masthead .ast-header-sticked .site-logo-img .astra-logo-svg { width: " + responsive_header_logo_width.tablet + "px; } } @media (max-width: " + mobile_break_point + "px) { #masthead .ast-header-sticked .site-logo-img .astra-logo-svg{ width: " + responsive_header_logo_width.mobile + "px; } } </style>";
		}else if( '' != header_logo_width ){
			var output = "<style type='text/css' id='ast-site-identity-img' class='ast-site-identity-img' > #masthead .ast-header-sticked .site-logo-img .astra-logo-svg { width: " + header_logo_width + "px; } #masthead .ast-header-sticked .site-logo-img img { max-height: " + id_height + "; width: auto; } </style>";
		}

		$("head").append( output );
	}

	// Any stick header is enabled?
	if ( stick_main || stick_above || stick_below ) {

		// Add Respective class to the body dependent on which sticky header is activated.
		$( document ).on( "addStickyClass", function() {
			var bodyClass = '';

			if ( '1' == stick_main || 'on' == stick_main || 'disabled' == stick_main ) {
				bodyClass += " ast-primary-sticky-header-active";
			}
			if ( '1' == stick_above || 'on' == stick_above || 'disabled' == stick_above ) {
				bodyClass += " ast-above-sticky-header-active";
			}
			if ( '1' == stick_below || 'on' == stick_below || 'disabled' == stick_below ) {
				bodyClass += " ast-below-sticky-header-active";
			}
			$('body').addClass(bodyClass);
		});

		// Remove Respective class from the body dependent on which sticky header is not activated.
		$( document ).on( "removeStickyClass", function() {
			var bodyClass = '';

			if ( '1' == stick_main || 'on' == stick_main || 'disabled' == stick_main ) {
				bodyClass += " ast-primary-sticky-header-active";
			}
			if ( '1' == stick_above || 'on' == stick_above || 'disabled' == stick_above ) {
				bodyClass += " ast-above-sticky-header-active";
			}
			if ( '1' == stick_below || 'on' == stick_below || 'disabled' == stick_below ) {
				bodyClass += " ast-below-sticky-header-active";
			}
			$('body').removeClass(bodyClass);
		});

		switch ( site_layout ) {
			case 'ast-box-layout':
				layout_width = parseInt( site_layout_box_width );
			break;
		}

		jQuery( document ).on('ready astLayoutWidthChanged', function( e ) {

			if( 'astLayoutWidthChanged' === e.type ) {

				// return if sticky not enabled.
				if (!(parseInt( stick_main ) || parseInt( stick_below ) || parseInt( stick_above ))) {
					return;
				}

				// Unwrapping sticky to reapply again.
				jQuery('div.ast-stick-primary-below-wrapper').children().unwrap();
				jQuery('div[data-stick-support="on"]').children().unwrap();

			}



			if ( '1' == sticky_hide_on_scroll ) {
	    		if ( '1' == main_shrink ) {
	    			jQuery( '#ast-fixed-header' ).addClass( 'ast-sticky-shrunk' ).stop();
	    		}

	    		if( !( '1' == stick_above || 'on' == stick_above || 'disabled' == stick_above ) ) {
					jQuery( '#ast-fixed-header .ast-above-header' ).hide();
				}
				if( !( '1' == stick_main || 'on' == stick_main || 'disabled' == stick_main ) ) {
					jQuery( '#ast-fixed-header .main-header-bar' ).hide();
				}
				if( !( '1' == stick_below || 'on' == stick_below || 'disabled' == stick_below ) ) {
					jQuery( '#ast-fixed-header .ast-below-header' ).hide();
				}

	    		jQuery( '#ast-fixed-header' ).astExtSticky({
					//dependent: ['#masthead .ast-above-header'],
					max_width: layout_width,
					site_layout: site_layout,
					sticky_on_device: sticky_header_on_devices,
					header_style: 'slide',
					hide_on_scroll: sticky_hide_on_scroll,
				});
			}else{
				if ('none' == sticky_header_style) {

					if (header_builder_active) {
						var headers = 'both' === sticky_header_on_devices ? ['desktop', 'mobile'] : [sticky_header_on_devices];

						headers.forEach(function (header) {

							/**
							 * Stick Above Header
							 */
							if ('1' == stick_above || 'on' == stick_above || 'disabled' == stick_above) {
								jQuery('#masthead #ast-' + header + '-header .ast-above-header').astExtSticky({
									max_width: layout_width,
									site_layout: site_layout,
									sticky_on_device: sticky_header_on_devices,
									header_style: sticky_header_style,
									hide_on_scroll: sticky_hide_on_scroll,
								});
							}
							// Add wrapper class to primary header & below header if stick primary header , stick below header and shrink primary header is enabled.
							// stick wrapper class of primary header and below header
							if (('1' == stick_main || 'on' == stick_main || 'disabled' == stick_main) &&
								('1' == stick_below || 'on' == stick_below || 'disabled' == stick_below)
							) {

								var selector = jQuery('#masthead #ast-' + header + '-header .main-header-bar-wrap').length ?
									jQuery('#masthead #ast-' + header + '-header .main-header-bar-wrap') :
									jQuery('#masthead #ast-' + header + '-header .ast-below-header-wrap');

								selector.wrap('<div class="ast-stick-primary-below-wrapper"></div>')
								jQuery('#masthead #ast-' + header + '-header .ast-below-header-wrap').prependTo('#masthead #ast-' + header + '-header .ast-stick-primary-below-wrapper');
								jQuery('#masthead #ast-' + header + '-header .main-header-bar-wrap').prependTo('#masthead #ast-' + header + '-header .ast-stick-primary-below-wrapper');

								jQuery('#masthead #ast-' + header + '-header .ast-stick-primary-below-wrapper').astExtSticky({
									dependent: ['#masthead #ast-' + header + '-header .ast-above-header'],
									max_width: layout_width,
									site_layout: site_layout,
									shrink: shrink_options,
									sticky_on_device: sticky_header_on_devices,
									header_style: sticky_header_style,
									hide_on_scroll: sticky_hide_on_scroll,
								});

							} else {

								/**
								 * Stick Main Header
								 */
								if ('1' == stick_main || 'on' == stick_main || 'disabled' == stick_main) {

									// If shrink is enabled
									// then add shrink top and bottom paddings.
									var shrink_options = '';
									if (main_shrink) {
										shrink_options = {
											padding_top: '',
											padding_bottom: '',
										}
									}

									jQuery('#masthead #ast-' + header + '-header .main-header-bar').astExtSticky({
										dependent: ['#masthead #ast-' + header + '-header .ast-above-header'],
										max_width: layout_width,
										site_layout: site_layout,
										shrink: shrink_options,
										sticky_on_device: sticky_header_on_devices,
										header_style: sticky_header_style,
										hide_on_scroll: sticky_hide_on_scroll,
									});

									jQuery('#masthead #ast-' + header + '-header .ast-custom-header').astExtSticky({
										max_width: layout_width,
										site_layout: site_layout,
										shrink: shrink_options,
										sticky_on_device: sticky_header_on_devices,
										header_style: sticky_header_style,
										hide_on_scroll: sticky_hide_on_scroll,
									});
								}

								/**
								 * Stick Below Header
								 */
								if (('1' == stick_below || 'on' == stick_below || 'disabled' == stick_below)) {
									jQuery('#masthead #ast-' + header + '-header .ast-below-header').astExtSticky({
										dependent: ['#masthead #ast-' + header + '-header .main-header-bar', '#masthead #ast-' + header + '-header .ast-above-header'],
										max_width: layout_width,
										site_layout: site_layout,
										sticky_on_device: sticky_header_on_devices,
										header_style: sticky_header_style,
										hide_on_scroll: sticky_hide_on_scroll,
									});

								}
							}

						});

					} else {

						/**
						 * Stick Above Header
						 */
						if ('1' == stick_above || 'on' == stick_above || 'disabled' == stick_above) {
							jQuery('#masthead .ast-above-header').astExtSticky({
								max_width: layout_width,
								site_layout: site_layout,
								sticky_on_device: sticky_header_on_devices,
								header_style: sticky_header_style,
								hide_on_scroll: sticky_hide_on_scroll,
							});
						}
						// Add wrapper class to primary header & below header if stick primary header , stick below header and shrink primary header is enabled.
						// stick wrapper class of primary header and below header
						if (('1' == stick_main || 'on' == stick_main || 'disabled' == stick_main) &&
							('1' == stick_below || 'on' == stick_below || 'disabled' == stick_below)
						) {

							jQuery('#masthead .main-header-bar-wrap').wrap('<div class="ast-stick-primary-below-wrapper"></div>')
							jQuery('#masthead .ast-below-header-wrap').prependTo('.ast-stick-primary-below-wrapper');
							jQuery('#masthead .main-header-bar-wrap').prependTo('.ast-stick-primary-below-wrapper');

							jQuery('#masthead .ast-stick-primary-below-wrapper').astExtSticky({
								dependent: ['#masthead .ast-above-header'],
								max_width: layout_width,
								site_layout: site_layout,
								shrink: shrink_options,
								sticky_on_device: sticky_header_on_devices,
								header_style: sticky_header_style,
								hide_on_scroll: sticky_hide_on_scroll,
							});

						} else {

							/**
							 * Stick Main Header
							 */
							if ('1' == stick_main || 'on' == stick_main || 'disabled' == stick_main) {

								// If shrink is enabled
								// then add shrink top and bottom paddings.
								var shrink_options = '';
								if (main_shrink) {
									shrink_options = {
										padding_top: '',
										padding_bottom: '',
									}
								}

								jQuery('#masthead .main-header-bar').astExtSticky({
									dependent: ['#masthead .ast-above-header'],
									max_width: layout_width,
									site_layout: site_layout,
									shrink: shrink_options,
									sticky_on_device: sticky_header_on_devices,
									header_style: sticky_header_style,
									hide_on_scroll: sticky_hide_on_scroll,
								});

								jQuery('#masthead .ast-custom-header').astExtSticky({
									max_width: layout_width,
									site_layout: site_layout,
									shrink: shrink_options,
									sticky_on_device: sticky_header_on_devices,
									header_style: sticky_header_style,
									hide_on_scroll: sticky_hide_on_scroll,
								});
							}

							/**
							 * Stick Below Header
							 */
							if (('1' == stick_below || 'on' == stick_below || 'disabled' == stick_below)) {
								jQuery('#masthead .ast-below-header').astExtSticky({
									dependent: ['#masthead .main-header-bar', '#masthead .ast-above-header'],
									max_width: layout_width,
									site_layout: site_layout,
									sticky_on_device: sticky_header_on_devices,
									header_style: sticky_header_style,
									hide_on_scroll: sticky_hide_on_scroll,
								});

							}
						}

					}

				}
		    	else{

					jQuery( '#ast-fixed-header' ).addClass( 'ast-sticky-shrunk' ).stop();

					if( !( '1' == stick_above || 'on' == stick_above || 'disabled' == stick_above ) ) {
						jQuery( '#ast-fixed-header .ast-above-header' ).hide();
					}
					if( !( '1' == stick_main || 'on' == stick_main || 'disabled' == stick_main ) ) {
						jQuery( '#ast-fixed-header .main-header-bar' ).hide();
					}
					if( !( '1' == stick_below || 'on' == stick_below || 'disabled' == stick_below) ) {
						jQuery( '#ast-fixed-header .ast-below-header' ).hide();
					}
					/**
			    	 * Stick Main Header
			    	 */
			    	if ( '1' == stick_above || 'on' == stick_above || 'disabled' == stick_above
						 || '1' == stick_main || 'on' == stick_main || 'disabled' == stick_main
						 || '1' == stick_below || 'on' == stick_below || 'disabled' == stick_below
						) {

			    		// If shrink is enabled
			    		// then add shrink top and bottom paddings.
		    			var shrink_options = '';
			    		if( main_shrink ) {
			    			shrink_options = {
					    		padding_top: '',
					    		padding_bottom: '',
					    	}
			    		}

					    jQuery( '#ast-fixed-header' ).astExtSticky({
					    	//dependent: ['#masthead .ast-above-header'],
					    	max_width: layout_width,
					    	site_layout: site_layout,
					    	shrink: shrink_options,
					    	sticky_on_device: sticky_header_on_devices,
					    	header_style: sticky_header_style,
					    	hide_on_scroll: sticky_hide_on_scroll,
					    });
			    	}
				}
			}

			// If Sticky Header for both mobile , desktops.
			if ( 'mobile' == sticky_header_on_devices || 'both' == sticky_header_on_devices ) {
				// Normal Header Mobile Menu Toggled
				jQuery( '#masthead .main-header-menu-toggle' ).click(function(event) {

					/* If menu navigation is opened and has sticky active */
					if( jQuery( '#masthead .main-header-menu-toggle' ).hasClass( 'toggled' ) ){
						// Add body class to update the stick_upto_scroll.
						$body.addClass('ast-sticky-toggled-off');

						if (
							'none' == defaults['header_style'] &&
							( jQuery( '#masthead .main-header-bar' ).hasClass('ast-sticky-active') ||
								jQuery( '#masthead .ast-stick-primary-below-wrapper' ).hasClass('ast-sticky-active') )
							){

							// Only If none style is selected
							var windowHeight = jQuery( window ).height(),
							headerSectionHeight = 0;

							if ( jQuery( '#masthead .ast-above-header' ) && jQuery( '#masthead .ast-above-header' ).length ) {
								headerSectionHeight = jQuery( '#masthead .ast-above-header' ).height();
							}

							// overflow hide for html.
							if ( '1' == sticky_hide_on_scroll ) {
								jQuery( 'html' ).css({
									'overflow'      : 'hidden',
								});
							}
					    	// add min height to wrapper class of primary header and below header
					    	if (  '1' == main_shrink &&
					    		( '1' == stick_main || 'on' == stick_main || 'disabled' == stick_main ) &&
					    		( '1' == stick_below || 'on' == stick_below || 'disabled' == stick_below )
					    	) {
								jQuery( '#masthead .ast-stick-primary-below-wrapper' ).css({
									'max-height'      : ( windowHeight - headerSectionHeight ) +'px',
									'overflow-y'      : 'auto',
								});
					    	} else {
								// ass max height to sticky header.
								jQuery( '#masthead .main-header-bar.ast-sticky-active' ).css({
									'max-height'      : ( windowHeight - headerSectionHeight ) +'px',
									'overflow-y'      : 'auto',
								});
							}
						}
					}
					else{
						$body.addClass('ast-sticky-toggled-off');
						jQuery( 'html' ).css({
							'overflow'      : '',
						});
						if (  '1' == main_shrink &&
					    		( '1' == stick_main || 'on' == stick_main || 'disabled' == stick_main ) &&
					    		( '1' == stick_below || 'on' == stick_below || 'disabled' == stick_below )
					    	) {
								jQuery( '#masthead .ast-stick-primary-below-wrapper' ).css({
									'max-height'      : '',
									'overflow-y'      : '',
								});
					    	} else {
								// ass max height to sticky header.
								jQuery( '#masthead .main-header-bar.ast-sticky-active' ).css({
									'max-height'      : '',
									'overflow-y'      : '',
								});
							}
					}
				});
				// Fixed Header Mobile Menu Toggled
				jQuery( '#ast-fixed-header .main-header-menu-toggle' ).click(function(event) {
					/* If menu navigation is opened and has sticky active */

					if( jQuery( '#ast-fixed-header .main-header-menu-toggle' ).hasClass( 'toggled' ) ){

						var windowHeight = jQuery( window ).height();

						// overflow hide for html.
						if ( '1' == sticky_hide_on_scroll ) {
							jQuery( 'html' ).css({
								'overflow'      : 'auto',
							});
						}
						// ass max height to sticky header.
						jQuery( '#ast-fixed-header' ).css({
							'max-height'      : ( windowHeight ) +'px',
							'overflow-y'      : 'auto',
						});
					}
					// remove css if menu toggle is closed.
					else{
						jQuery( 'html' ).css({
							'overflow'      : '',
						});
						jQuery( '#ast-fixed-header' ).css({
							'max-height'      : '',
							'overflow-y'      : '',
						});
					}
				});
			}

	    });
	}


}(jQuery, window));
/**
 *  Scroll To Top
 *
 * @package Astra Addon
 * @since  1.0.0
 */

document.addEventListener("DOMContentLoaded", function() { 

	var masthead        = document.querySelector( '#page header' );
	var astScrollTop 	= document.getElementById( 'ast-scroll-top' );
	if ( astScrollTop ) {
		astScrollToTop = function () {

			var content = getComputedStyle(astScrollTop).content,
				device  = astScrollTop.dataset.onDevices;
				content = content.replace( /[^0-9]/g, '' );

			if( 'both' == device || ( 'desktop' == device && '769' == content ) || ( 'mobile' == device && '' == content ) ) {

				// Get current window / document scroll.
				var  scrollTop = window.pageYOffset || document.body.scrollTop;
				// If masthead found.
				if( masthead && masthead.length ){
					if (scrollTop > masthead.offsetHeight + 100) {
						astScrollTop.style.display = "block";
					} else {
						astScrollTop.style.display = "none";
					}
				}
				else{
					// If there is no masthead set default start scroll
					if ( window.pageYOffset > 300 ) {
						astScrollTop.style.display = "block";
					} else {
						astScrollTop.style.display = "none";
					}	
				}
			} else {
				astScrollTop.style.display = "none";
			}
		};
		astScrollToTop();

		window.addEventListener('scroll', function () {
			astScrollToTop();
		});

		astScrollTop.onclick = function(e){
			e.preventDefault();

			window.scrollTo({
				top: 0,
				left: 0,
				behavior: 'smooth'
			});
		};
	}
});document.addEventListener("DOMContentLoaded", display_mega_menu_on_load);
document.addEventListener("astPartialContentRendered", display_mega_menu_on_load);

/**
 * Remove "hidden" class after the page is fully loaded to fix the visibility issue of MegaMenu width.
 */
function display_mega_menu_on_load() {
    // For Content width.
    var menu_content = document.querySelectorAll(".content-width-mega");
    if ( menu_content.length > 0 ) {
        for (var i = 0; i < menu_content.length; i++) {
            menu_content[i].addEventListener( "mouseenter", function( event ) {
                var mega_menu_container = event.target.querySelector(".astra-mega-menu-width-content");
                if ( null !== mega_menu_container ) {
                    mega_menu_container.classList.remove("ast-hidden");
                }
            });
        }
    }

    // For Menu Container width.
    var menu_container = document.querySelectorAll(".menu-container-width-mega");
    if ( menu_container.length > 0 ) {
        for (var i = 0; i < menu_container.length; i++) {
            menu_container[i].addEventListener( "mouseenter", function( event ) {
                var mega_menu_container = event.target.querySelector(".astra-mega-menu-width-menu-container");
                if ( null !== mega_menu_container ) {
                    mega_menu_container.classList.remove("ast-hidden");
                }
            });
        }
    }

    // For Full width.
    var menu_full_width = document.querySelectorAll(".full-width-mega");
    if ( menu_full_width.length > 0 ) {
		for (var i = 0; i < menu_full_width.length; i++) {
			menu_full_width[i].addEventListener( "mouseenter", function( event ) {
                var mega_menu_container = event.target.querySelector(".astra-full-megamenu-wrapper");
                var mega_menu_submenu = event.target.querySelector(".astra-mega-menu-width-full");
                if ( null !== mega_menu_container ) {
                    mega_menu_container.classList.remove("ast-hidden");
                }
                if ( null !== mega_menu_submenu ) {
                    mega_menu_submenu.classList.remove("ast-hidden");
                }
            });
        }
    }

    // For Full width Stretched.
    var menu_full_width_stretched = document.querySelectorAll(".full-stretched-width-mega");
    if ( menu_full_width_stretched.length > 0 ) {
        for (var i = 0; i < menu_full_width_stretched.length; i++) {
            menu_full_width_stretched[i].addEventListener( "mouseenter", function( event ) {
                var mega_menu_container = event.target.querySelector(".astra-full-megamenu-wrapper");
                var mega_menu_submenu = event.target.querySelector(".astra-mega-menu-width-full-stretched");
                if ( null !== mega_menu_container ) {
                    mega_menu_container.classList.remove("ast-hidden");
                }
                if ( null !== mega_menu_submenu ) {
                    mega_menu_submenu.classList.remove("ast-hidden");
                }
            });
        }
    }

    // For Custom Width MegaMenu.
    var customWidthStretched = document.querySelectorAll(".custom-width-mega");
    if ( customWidthStretched.length > 0 ) {
        for (var i = 0; i < customWidthStretched.length; i++) {
            customWidthStretched[i].addEventListener( "mouseenter", function( event ) {
                var megaMenuSubmenu = event.target.querySelector(".astra-mega-menu-width-custom");
                if ( null !== megaMenuSubmenu ) {
                    megaMenuSubmenu.classList.remove("ast-hidden");
                }
            });
        }
    }
}

var items = document.getElementsByClassName('astra-megamenu-li');

function apply_megamenu_width_styles() {
	[].slice.call(items).forEach(function(container) {
		jQuery( container ).hover( function() {

			var ast_container = jQuery(container).parents( '.ast-container' ),
				$main_container = ast_container.children(),
				$full_width_main_container = ast_container.parent(),
				$this            = jQuery( this );

			// Full width mega menu
			if( $this.hasClass( 'full-width-mega' ) || $this.hasClass( 'full-stretched-width-mega' ) ) {
				$main_container = jQuery( $main_container ).closest('.ast-container' );
			}

			if ( ( parseInt( jQuery(window).width() ) > parseInt( astra.break_point ) ) && 'ast-hf-mobile-menu' !== $this.parent().attr("id") && 'ast-desktop-toggle-menu' !== $this.parent().attr("id") ) {

				var $menuWidth           = $main_container.width(),
					$menuPosition        = $main_container.offset(),
					$menuItemPosition    = $this.offset(),
					positionLeft        = $menuItemPosition.left - ( $menuPosition.left + parseFloat($main_container.css('paddingLeft') ) );

				var $fullMenuWidth           = $full_width_main_container.width(),
					$fullMenuPosition        = $full_width_main_container.offset(),
					fullPositionLeft        = $menuItemPosition.left - ( $fullMenuPosition.left + parseFloat( $full_width_main_container.css( 'paddingLeft' ) ) );

				if( $this.hasClass( 'custom-width-mega' ) ) {
					var customMegaMenuWidth     = window.getComputedStyle( $this.find( '.astra-mega-menu-width-custom' )[0], '::before' ).getPropertyValue('content') || 1200;

					customMegaMenuWidth = customMegaMenuWidth.replace( /[^0-9]/g, '' );
					customMegaMenuWidth = parseInt( customMegaMenuWidth );

					if( customMegaMenuWidth <= $menuWidth ) {
						var extra_width = parseInt( $menuWidth - customMegaMenuWidth ),
							customWithPositionLeft = parseInt( positionLeft - extra_width );
					} else {
						var extra_width = parseInt( customMegaMenuWidth - $menuWidth ),
							customWithPositionLeft = parseInt( positionLeft + extra_width );
					}
				}

				if( $this.hasClass( 'menu-container-width-mega' ) ) {

					var menu_width_container = jQuery(container).parents( '.main-navigation' );

					if( $full_width_main_container.hasClass( 'ast-above-header' ) ) {
						menu_width_container = jQuery(".ast-above-header-navigation");
					} else if( $full_width_main_container.hasClass( 'ast-below-header' ) ) {
						menu_width_container = jQuery(".ast-below-header-actual-nav");
					}

					if ( menu_width_container.length ) {
						$target_container = menu_width_container;
					} else {
						$target_container = $this.parent( "ul" );
					}

					$menuWidth           = $target_container.width() + 'px';
					var $offset_right    = jQuery(window).width() - ( $target_container.offset().left + $target_container.outerWidth() );
					var $current_offset  = $this.offset();
					var $width           = ( jQuery(window).width() - $offset_right ) - $current_offset.left;
					positionLeft        = parseInt( $target_container.width() - $width );
				}
				if( $this.hasClass( 'full-width-mega' ) ) {
					$this.find( '.astra-full-megamenu-wrapper' ).css( { 'left': '-' + fullPositionLeft + 'px', 'width': $fullMenuWidth } );
					$this.find( '.astra-megamenu' ).css( { 'width': $menuWidth } );
				} else if( $this.hasClass( 'full-stretched-width-mega' ) ) {
					$this.find( '.astra-full-megamenu-wrapper' ).css( { 'left': '-'+fullPositionLeft+'px', 'width': $fullMenuWidth } );
				} else if( $this.hasClass( 'custom-width-mega' ) ) {
					$this.find( '.astra-mega-menu-width-custom' ).css( { 'left': '-' + customWithPositionLeft + 'px', 'width': customMegaMenuWidth+'px' } );
				} else {
					$this.find( '.astra-megamenu' ).css( { 'left': '-' + positionLeft + 'px', 'width': $menuWidth } );
				}
			} else {
				$this.find( '.astra-megamenu' ).css( { 'left': '', 'width': '', 'background-image': '' } );
				$this.find( '.astra-full-megamenu-wrapper' ).css( { 'left': '', 'width': '', 'background-image': '' } );
			}
		} );
	});
}

apply_megamenu_width_styles();

document.addEventListener( 'astMenuHoverStyleChanged', function() {
	apply_megamenu_width_styles();
} );

// Achieve accessibility for megamenus using focusin on <a>.
[].slice.call(items).forEach(function(container) {

    var ast_container = jQuery(container).parents( '.ast-container' ),
        $main_container = ast_container.children(),
        $full_width_main_container = ast_container.parent(),
        $this            = jQuery( container );

    // Full width mega menu
    if( $this.hasClass( 'full-width-mega' ) || $this.hasClass( 'full-stretched-width-mega' ) ) {
        $main_container = jQuery( $main_container ).closest('.ast-container' );
        $this.find( '.astra-full-megamenu-wrapper' ).removeClass( 'ast-hidden' );
    }

    $this.find( '.menu-link' ).focusin(function( e ) {
        $this.find( '.sub-menu' ).addClass( 'astra-megamenu-focus' );
        $this.find( '.sub-menu' ).removeClass( 'ast-hidden' );
        $this.find( '.astra-full-megamenu-wrapper' ).addClass( 'astra-megamenu-wrapper-focus' );
        if ( ( parseInt( jQuery(window).width() ) > parseInt( astra.break_point ) ) && 'ast-hf-mobile-menu' !== $this.parent().attr("id") && 'ast-desktop-toggle-menu' !== $this.parent().attr("id") ) {

            var $menuWidth           = $main_container.width(),
                $menuPosition        = $main_container.offset(),
                $menuItemPosition    = $this.offset(),
                positionLeft        = $menuItemPosition.left - ( $menuPosition.left + parseFloat($main_container.css('paddingLeft') ) );

            var $fullMenuWidth           = $full_width_main_container.width(),
                $fullMenuPosition        = $full_width_main_container.offset(),
                fullPositionLeft        = $menuItemPosition.left - ( $fullMenuPosition.left + parseFloat( $full_width_main_container.css( 'paddingLeft' ) ) );

            if( $this.hasClass( 'custom-width-mega' ) ) {
                var customMegaMenuWidth     = window.getComputedStyle( $this.find( '.astra-mega-menu-width-custom' )[0], '::before' ).getPropertyValue('content') || 1200;

                customMegaMenuWidth = customMegaMenuWidth.replace( /[^0-9]/g, '' );
                customMegaMenuWidth = parseInt( customMegaMenuWidth );

                if( customMegaMenuWidth <= $menuWidth ) {
                    var extra_width = parseInt( $menuWidth - customMegaMenuWidth ),
                        customWithPositionLeft = parseInt( positionLeft - extra_width );
                } else {
                    var extra_width = parseInt( customMegaMenuWidth - $menuWidth ),
                        customWithPositionLeft = parseInt( positionLeft + extra_width );
                }
            }

            if( $this.hasClass( 'menu-container-width-mega' ) ) {

                var menu_width_container = jQuery(container).parents( '.main-navigation' );

                if( $full_width_main_container.hasClass( 'ast-above-header' ) ) {
                    menu_width_container = jQuery(".ast-above-header-navigation");
                } else if( $full_width_main_container.hasClass( 'ast-below-header' ) ) {
                    menu_width_container = jQuery(".ast-below-header-actual-nav");
                }

                if ( menu_width_container.length ) {
                    $target_container = menu_width_container;
                } else {
                    $target_container = $this.parent( "ul" );
                }

                $menuWidth           = $target_container.width() + 'px';
                var $offset_right    = jQuery(window).width() - ( $target_container.offset().left + $target_container.outerWidth() );
                var $current_offset  = $this.offset();
                var $width           = ( jQuery(window).width() - $offset_right ) - $current_offset.left;
                var positionLeft        = parseInt( $target_container.width() - $width );
            }
            if( $this.hasClass( 'full-width-mega' ) ) {
                $this.find( '.astra-full-megamenu-wrapper' ).css( { 'left': '-' + fullPositionLeft + 'px', 'width': $fullMenuWidth } );
                $this.find( '.astra-megamenu' ).css( { 'width': $menuWidth } );
            } else if( $this.hasClass( 'full-stretched-width-mega' ) ) {
                $this.find( '.astra-full-megamenu-wrapper' ).css( { 'left': '-' + fullPositionLeft + 'px', 'width': $fullMenuWidth } );
            } else if( $this.hasClass( 'custom-width-mega' ) ) {
                $this.find( '.astra-mega-menu-width-custom' ).css( { 'left': '-' + customWithPositionLeft + 'px', 'width': customMegaMenuWidth+'px' } );
            } else {
                $this.find( '.astra-megamenu' ).css( { 'left': '-' + positionLeft + 'px', 'width': $menuWidth } );
            }
        } else {
            $this.find( '.astra-megamenu' ).css( { 'left': '', 'width': '', 'background-image': '' } );
            $this.find( '.astra-full-megamenu-wrapper' ).css( { 'left': '', 'width': '', 'background-image': '' } );
        }
    });

    $this.find( '.menu-link' ).keydown(function (e) {
    if (e.which  == 9 && e.shiftKey) {
        $this.find( '.sub-menu' ).removeClass( 'astra-megamenu-focus' );
        $this.find( '.astra-full-megamenu-wrapper' ).removeClass( 'astra-megamenu-wrapper-focus' );
    }
    });

    jQuery( container ).find( '.sub-menu .menu-item' ).last().focusout(function() {
        $this.find( '.sub-menu' ).removeClass( 'astra-megamenu-focus' );
        $this.find( '.astra-full-megamenu-wrapper' ).removeClass( 'astra-megamenu-wrapper-focus' );
    });

    jQuery(window).click(function() {
        $this.find( '.sub-menu' ).removeClass( 'astra-megamenu-focus' );
        $this.find( '.astra-full-megamenu-wrapper' ).removeClass( 'astra-megamenu-wrapper-focus' );
    });

    $this.click(function(event){
        if ( ! jQuery(event.target).hasClass('menu-item') ){
            return;
        }
        // event.stopPropagation();
        event.stopImmediatePropagation();
    });
});
/**
 * Advanced Search Styling
 *
 * @package Astra Addon
 * @since 1.4.8
 */

( function() {

	function body_iphone_classes() {
		var iphone = ( navigator.userAgent.match(/iPhone/i) == 'iPhone' ) ? 'iphone' : '';
		var ipod   = ( navigator.userAgent.match(/iPod/i) == 'iPod' ) ? 'ipod' : '';

		document.body.className += ' ' + iphone;
		document.body.className += ' ' + ipod;
	}
	body_iphone_classes();

	function remove_style_class( style ) {
		var allClasses = document.body.className;
		allClasses = allClasses.replace( style, '' );
    	document.body.className = allClasses;
	}

	function add_style_class( style ) {
		document.body.className += ' ' + style;
	}

	// Helper Function.
	function fade_in( element ) {

		element.style.display = 'block';
		setTimeout(function() {
			element.style.opacity = 1;
		}, 1);
	}

	function fade_out( element ) {

		element.style.opacity = '';
		setTimeout(function() {
			element.style.display = '';
		}, 200);
	}
	
	function header_cover_form_height( current_header_cover_form ) {

		// Primary header cover search.
		if ( document.body.classList.contains('ast-header-break-point') ) {
			
			var site_navigation = document.querySelector( '.main-navigation' );
			var main_header_bar = document.querySelector( '.main-header-bar' );

			if( null !== main_header_bar && null !== site_navigation ) {

				var site_navigation_outer_height = site_navigation.offsetHeight;
				var main_header_outer_height     = main_header_bar.offsetHeight;

				// Have a navigation outer height.
				// And primary header NOT have the `No Toggle` style.
				if( site_navigation_outer_height && ( ! document.body.classList.contains('ast-no-toggle-menu-enable') ) ) {
					var search_height = parseFloat(site_navigation_outer_height) - parseFloat(main_header_outer_height);
				} else {
					var search_height = parseFloat(main_header_outer_height);
				}
				current_header_cover_form.style.maxHeight = Math.abs( search_height ) + "px";
			}
		}
	}

	function header_builder_cover_form_height( current_header_cover_form ) {

		// Primary header cover search.
		if ( document.body.classList.contains('ast-header-break-point') ) {

			var site_navigation = document.querySelector( '.main-navigation' );
			var main_header_bar = document.querySelector( '.main-header-bar' );
			var mobile_header_bar = document.querySelector( '.ast-mobile-header-wrap' );

			if( null !== main_header_bar && null !== site_navigation ) {

				var site_navigation_outer_height = site_navigation.offsetHeight;
				var main_header_outer_height     = main_header_bar.offsetHeight;
				var mobile_header_outer_height     = mobile_header_bar.offsetHeight;

				// Have a navigation outer height.
				// And primary header NOT have the `No Toggle` style.
				if( site_navigation_outer_height && ( ! document.body.classList.contains('ast-no-toggle-menu-enable') ) ) {
					var search_height = parseFloat(site_navigation_outer_height) - parseFloat(main_header_outer_height);
				} else {
					var search_height = parseFloat(main_header_outer_height);
				}
				if ( current_header_cover_form.parentNode.classList.contains( 'ast-mobile-header-wrap' ) ) {
					var search_height = parseFloat(mobile_header_outer_height);
				}

				current_header_cover_form.style.maxHeight = Math.abs( search_height ) + "px";
			}
		}
	}

	var searchIcons = document.querySelectorAll( 'a.astra-search-icon:not(.slide-search)' );

	for ( var i = 0; searchIcons.length > i; i++ ) {

			searchIcons[i].onclick = function ( evt ) {

				evt.preventDefault();

				if ( ! evt ) {
					evt = window.event;
				}

				if ( this.classList.contains( 'header-cover' ) ) {
					var header_cover = document.querySelectorAll( '.ast-search-box.header-cover' ),
						header_builder_active 	 = astraAddon.is_header_builder_active || false;

					for (var j = 0; j < header_cover.length; j++) {

						var header_cover_icon = header_cover[j].parentNode.querySelectorAll( 'a.astra-search-icon' );

						for (var k = 0; k < header_cover_icon.length; k++) {
							if ( header_cover_icon[k] == this ) {
								fade_in( header_cover[j] );
								header_cover[j].querySelector( 'input.search-field' ).focus();

								// Set header cover form height.
								if ( header_builder_active ) {
									header_builder_cover_form_height( header_cover[j] );
								} else {
									header_cover_form_height( header_cover[j] );
								}
							}
						};
					};

				} else if ( this.classList.contains( 'full-screen' ) ) {

					var fullScreen = document.getElementById( 'ast-seach-full-screen-form' );
					if ( fullScreen.classList.contains( 'full-screen' ) ) {
						fade_in( fullScreen );
						add_style_class( 'full-screen' );
						fullScreen.querySelector( 'input.search-field' ).focus();
					}
				}
			};
	};

	/* Search Header Cover & Full Screen Close */
	var closes = document.querySelectorAll( '.ast-search-box .close' );
	for (var i = 0, len = closes.length; i < len; ++i) {
		closes[i].onclick = function(evt){

			if ( ! evt) { evt = window.event;
			}
			var self = this;
			while ( 1 ) {
				if ( self.parentNode.classList.contains( 'ast-search-box' ) ) {
					fade_out( self.parentNode );
					remove_style_class( 'full-screen' );
					break;
				} else if ( self.parentNode.classList.contains( 'site-header' ) ) {
					break;
				}
				self = self.parentNode;
			}
		};
	}

	document.onkeydown = function ( evt ) {
		if ( evt.keyCode == 27 ) {
			var fullScreenForm = document.getElementById( 'ast-seach-full-screen-form' );

			if ( null != fullScreenForm ) {
				fade_out( fullScreenForm );
				remove_style_class( 'full-screen' );
			}

			var header_cover = document.querySelectorAll( '.ast-search-box.header-cover' );
			for (var j = 0; j < header_cover.length; j++) {
				fade_out( header_cover[j] );
			}
		}
	}

	window.addEventListener("resize", function() {

		if( 'BODY' !== document.activeElement.tagName ) {
			return;
		}

		// Skip resize event when keyboard display event triggers on devices. 
		if( 'INPUT' != document.activeElement.tagName ) {
			var header_cover = document.querySelectorAll( '.ast-search-box.header-cover' );
			if ( ! document.body.classList.contains( 'ast-header-break-point' ) ) {
				for (var j = 0; j < header_cover.length; j++) {
					header_cover[j].style.maxHeight = '';
					header_cover[j].style.opacity = '';
					header_cover[j].style.display = '';
				}
			}
		}
	});

} )();
