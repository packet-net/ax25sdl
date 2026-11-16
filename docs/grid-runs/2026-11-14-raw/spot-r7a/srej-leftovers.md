| Scenario | Defect | Tables | Explorer | TLC | Agree | Expected |
| --- | --- | --- | --- | --- | --- | --- |
| g-ab3-ba3-b1-k4-nosrej-drop@inject=both,inject-frame=srej-command | grid | current | clean, 6981361 states | clean, 6981361 states (120956.6 s) | yes |  |
| g-ab3-ba3-b1-k4-nosrej-dup@inject=both,inject-frame=srej-command | grid | current | clean, 8669273 states | clean, 8669273 states (120798.6 s) | **no**: transitions fired: 11 by the explorer, 9 by TLC, 2 differ |  |
| g-ab3-ba3-b1-k4-nosrej-drop+dup@inject=both,inject-frame=srej-command | grid | current | clean, 13125122 states | clean, 13125122 states (160058.0 s) | **no**: transitions fired: 18 by the explorer, 12 by TLC, 6 differ |  |
| g-ab3-ba3-b1-k4-srej-dup@inject=both,inject-frame=srej-command | grid | current | clean, 8669273 states | clean, 8669273 states (120798.3 s) | **no**: transitions fired: 11 by the explorer, 9 by TLC, 2 differ |  |

Arm coverage over the current tables, across the 4 scenario(s) run on them: 29 of 280 rows fired (10%).
  transitions: TLC 18 of 248, explorer 18 of 248 (the same set)
  subroutine paths: TLC 11 of 32 (the explorer does not report paths)
Never fired, by state and input:
  AwaitingConnection
    DISC_received: t02_disc_received
    DM_received: t03_dm_received_yes, t03_dm_received_no
    UA_received: t04_ua_received_yes_yes, t04_ua_received_yes_no_yes, t04_ua_received_yes_no_no, t04_ua_received_no
    T1_expiry: t05_t1_expiry_yes, t05_t1_expiry_no
    all_other_primitives__from_lower_layer: t06_all_other_primitives__from_lower_layer
    DL_CONNECT_request: t07_dl_connect_request
    DL_UNIT_DATA_request: t08_dl_unit_data_request
    DL_DATA_request: t09_dl_data_request_yes, t09_dl_data_request_no
    I_frame_pops_off_queue: t10_i_frame_pops_off_queue_yes, t10_i_frame_pops_off_queue_no
    all_other_primitives__from_upper_layer: t11_all_other_primitives__from_upper_layer
    UI_received: t12_ui_received_yes, t12_ui_received_no
    control_field_error: t13_control_field_error
    info_not_permitted_in_frame: t14_info_not_permitted_in_frame
    u_or_s_frame_length_error: t15_u_or_s_frame_length_error
    SABM_received: t16_sabm_received
    SABME_received: t17_sabme_received
  AwaitingRelease
    DL_DISCONNECT_request: t01_dl_disconnect_request
    T1_expiry: t02_t1_expiry_yes, t02_t1_expiry_no
    UA_received: t03_ua_received_yes, t03_ua_received_no
    all_other_primitives__from_lower_layer: t04_all_other_primitives__from_lower_layer
    DL_UNIT_DATA_request: t05_dl_unit_data_request
    all_other_primitives__from_upper_layer: t06_all_other_primitives__from_upper_layer
    control_field_error: t07_control_field_error
    info_not_permitted_in_frame: t08_info_not_permitted_in_frame
    u_or_s_frame_length_error: t09_u_or_s_frame_length_error
    SABM_received: t10_sabm_received
    SABME_received: t11_sabme_received
    DISC_received: t12_disc_received
    DM_received: t13_dm_received_yes, t13_dm_received_no
    UI_received: t14_ui_received_yes, t14_ui_received_no
    i_or_s_command_received: t15_i_or_s_command_received_yes, t15_i_or_s_command_received_no
  AwaitingV22Connection
    DL_CONNECT_request: t02_dl_connect_request
    DL_UNIT_DATA_request: t03_dl_unit_data_request
    DL_DATA_request: t04_dl_data_request_yes, t04_dl_data_request_no
    I_frame_pops_off_queue: t05_i_frame_pops_off_queue_yes, t05_i_frame_pops_off_queue_no
    all_other_primitives__from_upper_layer: t06_all_other_primitives__from_upper_layer
    control_field_error: t07_control_field_error
    info_not_permitted_in_frame: t08_info_not_permitted_in_frame
    u_or_s_frame_length_error: t09_u_or_s_frame_length_error
    UI_received: t10_ui_received_yes, t10_ui_received_no
    DM_received: t11_dm_received_yes, t11_dm_received_no
    UA_received: t12_ua_received_yes_yes, t12_ua_received_yes_no_yes, t12_ua_received_yes_no_no, t12_ua_received_no
    T1_expiry: t13_t1_expiry_yes, t13_t1_expiry_no
    FRMR_received: t14_frmr_received
    SABME_received: t15_sabme_received
    SABM_received: t16_sabm_received
    DISC_received: t17_disc_received
    all_other_primitives__from_lower_layer: t18_all_other_primitives__from_lower_layer
  Connected
    DL_DISCONNECT_request: t01_dl_disconnect_request
    DL_DATA_request: t02_dl_data_request
    I_frame_pops_off_queue: t03_i_frame_pops_off_queue_yes, t03_i_frame_pops_off_queue_no_yes
    DL_UNIT_DATA_request: t04_dl_unit_data_request
    DL_FLOW_OFF_request: t05_dl_flow_off_request_yes, t05_dl_flow_off_request_no
    DL_FLOW_ON_request: t06_dl_flow_on_request_yes_yes, t06_dl_flow_on_request_yes_no, t06_dl_flow_on_request_no
    DL_CONNECT_request: t07_dl_connect_request_yes, t07_dl_connect_request_no
    all_other_primitives__from_upper_layer: t08_all_other_primitives__from_upper_layer
    control_field_error: t09_control_field_error_yes, t09_control_field_error_no
    info_not_permitted_in_frame: t10_info_not_permitted_in_frame_yes, t10_info_not_permitted_in_frame_no
    u_or_s_frame_length_error: t11_u_or_s_frame_length_error_yes, t11_u_or_s_frame_length_error_no
    T3_expiry: t13_t3_expiry
    SABM_received: t14_sabm_received_yes, t14_sabm_received_no
    SABME_received: t15_sabme_received_yes, t15_sabme_received_no
    FRMR_received: t16_frmr_received_yes, t16_frmr_received_no
    UA_received: t17_ua_received_yes, t17_ua_received_no
    UI_received: t18_ui_received_yes, t18_ui_received_no
    DISC_received: t19_disc_received
    DM_received: t20_dm_received
    RR_received: t21_rr_received_no_yes, t21_rr_received_no_no
    RNR_received: t22_rnr_received_yes, t22_rnr_received_no_yes, t22_rnr_received_no_no
    SREJ_received: t24_srej_received_yes_yes, t24_srej_received_no_yes, t24_srej_received_no_no
    REJ_received: t25_rej_received_no_yes, t25_rej_received_no_no
    I_received: t26_i_received_yes_yes_yes_yes_yes, t26_i_received_yes_yes_yes_yes_no, t26_i_received_yes_yes_yes_no_yes_yes, t26_i_received_yes_yes_yes_no_no_yes_yes_yes, t26_i_received_yes_yes_yes_no_no_yes_no_yes_yes, t26_i_received_yes_yes_yes_no_no_yes_no_yes_no_yes, t26_i_received_yes_yes_yes_no_no_yes_no_yes_no_no, t26_i_received_yes_yes_yes_no_no_no_yes, t26_i_received_yes_yes_no_yes, t26_i_received_yes_yes_no_no, t26_i_received_yes_no_yes, t26_i_received_yes_no_no, t26_i_received_no
  Disconnected
    DL_DISCONNECT_request: t01_dl_disconnect_request
    DL_UNIT_DATA_request: t02_dl_unit_data_request
    DL_CONNECT_request: t03_dl_connect_request
    all_other_primitives__from_upper_layer: t04_all_other_primitives__from_upper_layer
    all_other_commands: t05_all_other_commands
    all_other_primitives__from_lower_layer: t06_all_other_primitives__from_lower_layer
    control_field_error: t07_control_field_error
    info_not_permitted_in_frame: t08_info_not_permitted_in_frame
    u_or_s_frame_length_error: t09_u_or_s_frame_length_error
    UA_received: t10_ua_received
    UI_received: t11_ui_received_yes, t11_ui_received_no
    DISC_received: t12_disc_received
    SABM_received: t13_sabm_received_yes, t13_sabm_received_no
    SABME_received: t14_sabme_received_yes, t14_sabme_received_no
  subroutine Set_Version_2_2
    (paths): t01_set_version_2_2
  subroutine UI_Check
    (paths): t01_ui_check_yes_yes, t02_ui_check_yes_no, t03_ui_check_no
  subroutine Select_T1
    (paths): t02_select_t1_no_yes
  subroutine Set_Version_2_0
    (paths): t01_set_version_2_0
  subroutine Check_Need_for_Response
    (paths): t02_check_need_for_response_no_yes
  subroutine Establish_Extended_Data_Link
    (paths): t01_establish_extended_data_link_yes, t02_establish_extended_data_link_no
  subroutine Establish_Data_Link
    (paths): t01_establish_data_link_yes, t02_establish_data_link_no
  subroutine Check_I_Frame_Acknowledged
    (paths): t01_check_i_frame_acknowledged_yes_yes, t02_check_i_frame_acknowledged_yes_no
  subroutine Clear_Exception_Conditions
    (paths): t01_clear_exception_conditions
  subroutine N_r_Error_Recovery
    (paths): t01_n_r_error_recovery
  subroutine Transmit_Enquiry
    (paths): t01_transmit_enquiry_yes
  subroutine Enquiry_Response
    (paths): t01_enquiry_response_yes_yes, t02_enquiry_response_yes_no_yes_yes_yes, t03_enquiry_response_yes_no_yes_yes_no, t04_enquiry_response_yes_no_yes_no_yes, t05_enquiry_response_yes_no_yes_no_no
  TimerRecovery
    DL_DISCONNECT_request: t01_dl_disconnect_request
    DL_DATA_request: t02_dl_data_request
    I_frame_pops_off_queue: t03_i_frame_pops_off_queue_yes, t03_i_frame_pops_off_queue_no_yes, t03_i_frame_pops_off_queue_no_no_yes, t03_i_frame_pops_off_queue_no_no_no
    DL_UNIT_DATA_request: t04_dl_unit_data_request
    DL_FLOW_OFF_request: t05_dl_flow_off_request_yes, t05_dl_flow_off_request_no
    DL_FLOW_ON_request: t06_dl_flow_on_request_yes_yes, t06_dl_flow_on_request_yes_no, t06_dl_flow_on_request_no
    DL_CONNECT_request: t07_dl_connect_request
    control_field_error: t08_control_field_error
    info_not_permitted_in_frame: t09_info_not_permitted_in_frame
    u_or_s_frame_length_error: t10_u_or_s_frame_length_error
    UA_received: t11_ua_received
    DM_received: t12_dm_received
    SABM_received: t13_sabm_received_yes, t13_sabm_received_no
    SABME_received: t14_sabme_received_yes, t14_sabme_received_no
    FRMR_received: t15_frmr_received
    UI_received: t16_ui_received_yes, t16_ui_received_no
    DISC_received: t17_disc_received
    RR_received: t18_rr_received_yes_no_yes, t18_rr_received_yes_no_no, t18_rr_received_no_yes_yes, t18_rr_received_no_yes_no_yes, t18_rr_received_no_yes_no_no, t18_rr_received_no_no_no_yes, t18_rr_received_no_no_no_no
    RNR_received: t19_rnr_received_yes_yes_yes, t19_rnr_received_yes_yes_no, t19_rnr_received_yes_no_yes, t19_rnr_received_yes_no_no, t19_rnr_received_no_yes_yes, t19_rnr_received_no_yes_no_yes, t19_rnr_received_no_yes_no_no, t19_rnr_received_no_no_yes, t19_rnr_received_no_no_no_yes, t19_rnr_received_no_no_no_no
    LM_SEIZE_confirm: t20_lm_seize_confirm_yes, t20_lm_seize_confirm_no
    T1_expiry: t21_t1_expiry_yes_yes_yes, t21_t1_expiry_yes_yes_no, t21_t1_expiry_yes_no
    I_received: t22_i_received_yes_yes_yes_yes_yes, t22_i_received_yes_yes_yes_yes_no, t22_i_received_yes_yes_yes_no_yes_yes, t22_i_received_yes_yes_yes_no_yes_no_yes, t22_i_received_yes_yes_yes_no_yes_no_no, t22_i_received_yes_yes_yes_no_no_yes_yes_yes, t22_i_received_yes_yes_yes_no_no_yes_yes_no, t22_i_received_yes_yes_yes_no_no_yes_no_yes_yes, t22_i_received_yes_yes_yes_no_no_yes_no_yes_no_yes, t22_i_received_yes_yes_yes_no_no_yes_no_yes_no_no, t22_i_received_yes_yes_yes_no_no_yes_no_no, t22_i_received_yes_yes_yes_no_no_no_yes, t22_i_received_yes_yes_yes_no_no_no_no, t22_i_received_yes_yes_no, t22_i_received_yes_no, t22_i_received_no
    REJ_received: t23_rej_received_yes_yes_yes, t23_rej_received_yes_yes_no, t23_rej_received_yes_no_yes, t23_rej_received_yes_no_no, t23_rej_received_no_yes_yes_yes, t23_rej_received_no_yes_yes_no, t23_rej_received_no_yes_no_yes, t23_rej_received_no_yes_no_no, t23_rej_received_no_no_yes_yes, t23_rej_received_no_no_yes_no, t23_rej_received_no_no_no_yes, t23_rej_received_no_no_no_no
    SREJ_received: t24_srej_received_yes_yes_yes_yes, t24_srej_received_yes_yes_yes_no, t24_srej_received_yes_yes_no_yes, t24_srej_received_yes_yes_no_no, t24_srej_received_yes_no_yes, t24_srej_received_yes_no_no, t24_srej_received_no_yes_yes_yes, t24_srej_received_no_yes_yes_no, t24_srej_received_no_yes_no_yes, t24_srej_received_no_no_yes, t24_srej_received_no_no_no
    all_other_primitives__from_lower_layer: t25_all_other_primitives__from_lower_layer
    all_other_primitives__from_upper_layer: t26_all_other_primitives__from_upper_layer

Grid summary: 4 cell(s): 1 agree, 3 disagree, 0 capped (not compared), 0 timed out (rerun with --resume --retry-timeouts and a larger --timeout), 0 failed (rerun with --resume).
Verdicts (explorer): clean 4.
Time: explorer 3056 s, TLC 522611 s in all; largest cell 13125122 distinct states.

